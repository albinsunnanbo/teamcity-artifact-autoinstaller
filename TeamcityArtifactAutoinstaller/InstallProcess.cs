﻿using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace TeamcityArtifactAutoinstaller
{
    public static class InstallProcess
    {
        private static Dictionary<string, string> lastCheckedVersion = new Dictionary<string, string>();
        private static readonly ILog log = LogManager.GetLogger(typeof(InstallProcess));

        public static void CheckAndInstall()
        {
            Console.WriteLine("Check and install");
            log.Debug("CheckAndInstall BEGIN");

            foreach (var project in Properties.Settings.Default.TeamcityProjects)
            {
                log.DebugFormat("Start project {0}", project.TeamCityProjectId);

                using (WebClient wc = new WebClient())
                {
                    var sw = Stopwatch.StartNew();
                    var timeStats = new StringBuilder();
                    wc.Credentials = new NetworkCredential(project.TeamCityUserName, project.TeamCityPassword);
                    var versionUrl = string.Format("{0}/httpAuth/app/rest/buildTypes/id:{1}/builds/status:SUCCESS/number", project.TeamCityBaseUrl, project.TeamCityProjectId);
                    var versionString = wc.DownloadString(versionUrl);
                    timeStats.AppendLine(sw.Elapsed.ToString() + " got versionString");

                    log.DebugFormat("versionString = {0}", versionString);

                    if (lastCheckedVersion.ContainsKey(project.TeamCityProjectId))
                    {
                        if (lastCheckedVersion[project.TeamCityProjectId] == versionString)
                        {
                            // Nothing has changed
                            log.DebugFormat("Nothing has changed");
                            continue;
                        }
                    }
                    else
                    {
                        // Startup, just remember current version but don't install
                        lastCheckedVersion[project.TeamCityProjectId] = versionString;
                        log.InfoFormat("Startup setting baseline version {0} for project {1}", versionString, project.TeamCityProjectId);
                        continue;
                    }

                    // We found a new version for this project

                    // Download artifact
                    var artifactUrl = string.Format("{0}/repository/downloadAll/{1}/{2}", project.TeamCityBaseUrl, project.TeamCityProjectId, versionString);
                    var zipFileName = string.Format("{0}_{1}.zip", project.TeamCityProjectId, versionString);
                    var unzipDir = string.Format("{0}_{1}", project.TeamCityProjectId, versionString);
                    var zipPath = Path.Combine(project.InstallPath, zipFileName);
                    var unzipDirPath = Path.Combine(project.InstallPath, unzipDir);
                    wc.DownloadFile(artifactUrl, zipPath);
                    timeStats.AppendLine(sw.Elapsed.ToString() + " file downloaded");


                    log.InfoFormat("File {0} downloaded", artifactUrl);

                    ZipFile.ExtractToDirectory(zipPath, unzipDirPath);
                    File.Delete(zipPath);
                    timeStats.AppendLine(sw.Elapsed.ToString() + " file extracted");

                    log.InfoFormat("File unzipped to {0}", unzipDirPath);

                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.WorkingDirectory = project.InstallPath;
                    startInfo.FileName = Path.Combine(project.InstallPath, project.InstallCommand);
                    startInfo.Arguments = unzipDir;
                    startInfo.RedirectStandardOutput = true;
                    startInfo.UseShellExecute = false;

                    log.InfoFormat("Starting {0} with arguments {1}", startInfo.FileName, startInfo.Arguments);

                    var process = Process.Start(startInfo);

                    process.WaitForExit(5 * 60 * 1000); // wait 5 minutes
                    timeStats.AppendLine(sw.Elapsed.ToString() + " install script executed");

                    log.Info("Process execution done");

                    var stdOutResponse = process.StandardOutput.ReadToEnd();
                    timeStats.AppendLine(sw.Elapsed.ToString() + " parsed install script output");

                    log.Info("Start sending mails");
                    MailMessage m = new MailMessage();
                    foreach (var recipient in Properties.Settings.Default.NotificationEmails)
                    {
                        m.To.Add(new MailAddress(recipient));
                    }
                    var hasFailed = false;
                    if (process.ExitCode != 0)
                    {
                        m.Subject = string.Format("FAILED DEPLOY {0} version {1} failed with ExitCode = {2}", project.TeamCityProjectId, versionString, process.ExitCode);
                        log.Info(m.Subject);
                        hasFailed = true;
                    }

                    if (!hasFailed)
                    {
                        if (!string.IsNullOrEmpty(project.VerifyUrl))
                        {
                            wc.Credentials = null;
                            var verifyPage = wc.DownloadString(project.VerifyUrl);

                            if (!verifyPage.Contains(versionString))
                            {
                                m.Subject = string.Format("FAILED DEPLOY {0} version {1} could not verify {2}", project.TeamCityProjectId, versionString, project.VerifyUrl);
                                log.Info(m.Subject);
                                log.InfoFormat("Verify page content\n{0}", verifyPage);
                                hasFailed = true;
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
                    using (var attachementStream = new MemoryStream(Encoding.UTF8.GetBytes(stdOutResponse)))
                    {
                        m.Attachments.Add(new Attachment(attachementStream, "install-log.txt"));

                        SmtpClient smtp = new SmtpClient();
                        smtp.Send(m);
                    }

                    log.Info("Mail sending done");
                    timeStats.AppendLine(sw.Elapsed.ToString() + " sent all mails");
                    log.Info("Time stats:" + Environment.NewLine + timeStats.ToString());
                    // Store current version
                    lastCheckedVersion[project.TeamCityProjectId] = versionString;
                }
            }

            log.Debug("CheckAndInstall END");
        }
    }
}
