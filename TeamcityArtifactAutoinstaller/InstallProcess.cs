using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace TeamcityArtifactAutoinstaller
{
    public static class InstallProcess
    {
        private static Dictionary<string, string> lastCheckedVersion = new Dictionary<string, string>();
        private static readonly ILog log = LogManager.GetLogger(typeof(InstallProcess));

        public static void CheckAndInstall(bool interactive)
        {
            Console.WriteLine("Check and install");
            log.Debug("CheckAndInstall BEGIN");

            foreach (var project in Properties.Settings.Default.TeamcityProjects)
            {
                string versionString = null;
                try
                {
                    log.DebugFormat("Start project {0}", project.TeamCityProjectId);

                    using (WebClient wc = new WebClient())
                    {
                        var sw = Stopwatch.StartNew();
                        var timeStats = new StringBuilder();
                        wc.Credentials = new NetworkCredential(project.TeamCityUserName, project.TeamCityPassword);
                        var versionUrl = string.Format("{0}/httpAuth/app/rest/buildTypes/id:{1}/builds/status:SUCCESS/number", project.TeamCityBaseUrl, project.TeamCityProjectId);
                        versionString = wc.DownloadString(versionUrl);
                        timeStats.AppendLine(sw.Elapsed.ToString() + " got versionString");

                        log.DebugFormat("versionString = {0}", versionString);

                        if (lastCheckedVersion.ContainsKey(project.TeamCityProjectId))
                        {
                            if (lastCheckedVersion[project.TeamCityProjectId] == versionString)
                            {
                                // Nothing has changed
                                log.DebugFormat("Nothing has changed");
                                Console.WriteLine("Nothing has changed");
                                if (interactive)
                                {
                                    Console.WriteLine("Press y to install anyway, any other key to skip install");
                                    if (Console.ReadKey().Key != ConsoleKey.Y)
                                    {
                                        continue;
                                    }
                                }
                                else
                                {
                                    continue;
                                }
                            }
                        }
                        else
                        {
                            // Startup, just remember current version but don't install
                            lastCheckedVersion[project.TeamCityProjectId] = versionString;
                            log.InfoFormat("Startup setting baseline version {0} for project {1}", versionString, project.TeamCityProjectId);
                            Console.WriteLine(string.Format("Startup setting baseline version {0} for project {1}", versionString, project.TeamCityProjectId));
                            if (interactive)
                            {
                                Console.WriteLine("Press y to install anyway, any other key to skip install");
                                if (Console.ReadKey().Key != ConsoleKey.Y)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                continue;
                            }
                        }

                        // We found a new version for this project

                        // Download artifact
                        var artifactUrl = string.Format("{0}/repository/downloadAll/{1}/{2}", project.TeamCityBaseUrl, project.TeamCityProjectId, versionString);
                        var zipFileName = string.Format("{0}_{1}.zip", project.TeamCityProjectId, versionString);
                        var unzipDir = string.Format("{0}_{1}", project.TeamCityProjectId, versionString);
                        var zipPath = Path.Combine(project.InstallPath, zipFileName);
                        var unzipDirPath = Path.Combine(project.InstallPath, unzipDir);
                        if (!Directory.Exists(unzipDirPath))
                        {
                            Directory.CreateDirectory(unzipDirPath);
                        }
                        wc.DownloadFile(artifactUrl, zipPath);
                        timeStats.AppendLine(sw.Elapsed.ToString() + " file downloaded");


                        log.InfoFormat("File {0} downloaded", artifactUrl);

                        ZipFile.ExtractToDirectory(zipPath, unzipDirPath);
                        File.Delete(zipPath);
                        timeStats.AppendLine(sw.Elapsed.ToString() + " file extracted");

                        log.InfoFormat("File unzipped to {0}", unzipDirPath);

                        ProcessStartInfo startInfo = new ProcessStartInfo();

                        // Use install.bat contained in zip archive as a starting point for the installation.
                        if (project.ArchiveContainsInstallCommand)
                        {
                            startInfo.WorkingDirectory = unzipDirPath;
                        }
                        else
                        {
                            startInfo.WorkingDirectory = project.InstallPath;                            
                        }
                        startInfo.FileName = Path.Combine(startInfo.WorkingDirectory, project.InstallCommand);

                        startInfo.Arguments = unzipDir;
                        startInfo.RedirectStandardOutput = true;
                        startInfo.UseShellExecute = false;

                        log.InfoFormat("Starting {0} with arguments {1}", startInfo.FileName, startInfo.Arguments);

                        using (var process = new Process())
                        {
                            process.StartInfo = startInfo;
                            var stdOutResponse = new StringBuilder();
                            process.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
                            {
                                if (e.Data != null)
                                {
                                    stdOutResponse.AppendLine(e.Data);
                                }
                            };

                            process.Start();
                            process.BeginOutputReadLine();

                            timeStats.AppendLine(sw.Elapsed.ToString() + " parsed install script output");

                            var processTerminatedSuccessfully = process.WaitForExit(5 * 60 * 1000); // wait 5 minutes
                            if (processTerminatedSuccessfully)
                            {
                                process.WaitForExit(); // Force reading all output since that may not be read completely with the above overload
                                //                        Recommended by http://msdn.microsoft.com/en-us/library/ty0d8k56(v=vs.110).aspx
                            }
                            timeStats.AppendLine(sw.Elapsed.ToString() + " install script executed");

                            log.Info("Process execution done");

                            log.Info("Start sending mails");
                            MailMessage m = new MailMessage();
                            foreach (var recipient in Properties.Settings.Default.NotificationEmails)
                            {
                                m.To.Add(new MailAddress(recipient));
                            }
                            var hasFailed = false;
                            if (!processTerminatedSuccessfully)
                            {
                                m.Subject = string.Format("FAILED DEPLOY {0} version {1} timed out and was terminated", project.TeamCityProjectId, versionString);
                                log.Info(m.Subject);
                                hasFailed = true;
                            }
                            if (!hasFailed && process.ExitCode != 0)
                            {
                                m.Subject = string.Format("FAILED DEPLOY {0} version {1} failed with ExitCode = {2}", project.TeamCityProjectId, versionString, process.ExitCode);
                                log.Info(m.Subject);
                                hasFailed = true;
                            }

                            string errorVerifyPage = "";
                            if (!hasFailed)
                            {
                                if (!string.IsNullOrEmpty(project.VerifyUrl))
                                {
                                    wc.Credentials = null;
                                    var verifyPage = wc.DownloadString(project.VerifyUrl);

                                    var shortVersionString = versionString;
                                    if (shortVersionString.Length > 9)
                                    {
                                        shortVersionString = shortVersionString.Substring(0, 9);
                                    }
                                    if (!verifyPage.Contains(shortVersionString))
                                    {
                                        m.Subject = string.Format("FAILED DEPLOY {0} version {1} could not verify {2}", project.TeamCityProjectId, versionString, project.VerifyUrl);
                                        log.Info(m.Subject);
                                        log.InfoFormat("Verify page content\n{0}", verifyPage);
                                        hasFailed = true;
                                        errorVerifyPage = verifyPage;
                                    }
                                    timeStats.AppendLine(sw.Elapsed.ToString() + " verify complete");
                                }
                            }
                            if (!hasFailed)
                            {
                                m.Subject = string.Format("{0} version {1} was automatically deployed", project.TeamCityProjectId, versionString);
                            }

                            m.Body = Properties.Settings.Default.MailBody
                                + Environment.NewLine + Environment.NewLine + "Time stats:" + Environment.NewLine + timeStats.ToString();
                            using (var attachementStream = new MemoryStream(Encoding.UTF8.GetBytes(stdOutResponse.ToString())))
                            {
                                using (var errorAttachementStream = new MemoryStream(Encoding.UTF8.GetBytes(errorVerifyPage)))
                                {
                                    m.Attachments.Add(new Attachment(attachementStream, "install-log.txt"));
                                    if (!string.IsNullOrEmpty(errorVerifyPage))
                                    {
                                        m.Attachments.Add(new Attachment(errorAttachementStream, "verify-fail-page-content.txt"));
                                    }

                                    SmtpClient smtp = new SmtpClient();
                                    smtp.Send(m);
                                }
                            }

                            log.Info("Mail sending done");
                            timeStats.AppendLine(sw.Elapsed.ToString() + " sent all mails");
                            log.Info("Time stats:" + Environment.NewLine + timeStats.ToString());

                            // Store current version
                            lastCheckedVersion[project.TeamCityProjectId] = versionString;
                        }
                    }
                }
                catch (WebException e)
                {
                    log.Error("Installation failed with WebException", e);
                    Console.WriteLine("Installation failed with WebException");
                    // deliberately don't send any mail
                }
                catch (Exception e)
                {
                    // Store current version to avoid endless failure loops
                    if (!string.IsNullOrEmpty(versionString))
                    {
                        lastCheckedVersion[project.TeamCityProjectId] = versionString;
                    }
                    
                    log.Error("Installation failed with unknown exception", e);
                    Console.WriteLine("Installation failed with unknown exception");
                    Console.WriteLine(e.ToString());
                    try
                    {
                        MailMessage m = new MailMessage();
                        foreach (var recipient in Properties.Settings.Default.NotificationEmails)
                        {
                            m.To.Add(new MailAddress(recipient));
                        }

                        m.Subject = string.Format("FAILED DEPLOY {0} version {1} with unknown exception", project.TeamCityProjectId, versionString);

                        m.Body = e.ToString();
                        SmtpClient smtp = new SmtpClient();
                        smtp.Send(m);
                    }
                    catch (Exception mailEx)
                    {
                        log.Error("Exception mail sending failed", mailEx);
                        Console.WriteLine("Exception mail sending failed");
                        Console.WriteLine(mailEx.ToString());
                    }
                }
                log.Debug("CheckAndInstall END");
            }
        }
    }
}
