﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AccuRev2Git
{
	class Program
	{
		private static List<GitUser> _gitUsers;

		public static void Main(string[] args)
		{
			if (args.Length == 0)
			{
				throw new ApplicationException("You must specify some arguments - this app isn't a mind reader!");
			}

			var depotName = args[0];
			var workingDir = args[1];
			var startingTran = (args.Length > 2 ? Int32.Parse(args[2]) : 0);
			if (string.IsNullOrEmpty(workingDir))
				throw new ApplicationException(string.Format("No value specified for working directory."));

			loadUsers();
			loadDepotFromScratch(depotName, workingDir, startingTran);
		}

		private static void loadUsers()
		{
			if (!File.Exists("users.txt"))
				throw new ApplicationException("No \"users.txt\" file found!");

			var usersRaw = File.ReadAllLines("users.txt");
			_gitUsers = new List<GitUser>(usersRaw.Length);
			foreach (var parts in usersRaw.Where(userRaw => !string.IsNullOrEmpty(userRaw)).Select(userRaw => userRaw.Split('|')))
				_gitUsers.Add(new GitUser(parts[0], parts[1], parts[2]));
		}

		static void loadDepotFromScratch(string depotName, string workingDir, int startingTransaction)
		{
			var xdoc = new XDocument();

			var tempFile = string.Format("_{0}_.depot.hist.xml", depotName);
			if (File.Exists(tempFile))
			{
				Console.Write("Existing history file found. Re-use it? [y|n] ");
// ReSharper disable PossibleNullReferenceException
				if (Console.ReadLine().ToUpper() == "Y")
// ReSharper restore PossibleNullReferenceException
				{
					xdoc = XDocument.Load(tempFile);
				}
			}

			if (xdoc.Document == null || xdoc.Nodes().Any() == false)
			{
				Console.WriteLine(string.Format("Retrieving complete history of {0} depot from AccuRev server...", depotName));
				var temp = execAccuRev(string.Format("hist -p {0} -s {0}_dev -k promote -fx", depotName), workingDir);
				File.WriteAllText(tempFile, temp);
				xdoc = XDocument.Parse(temp);
			}

			var n = 0;
// ReSharper disable PossibleNullReferenceException
			var nodes = xdoc.Document.Descendants("transaction").Where(t => Int32.Parse(t.Attribute("id").Value) >= startingTransaction).OrderBy(t => Int32.Parse(t.Attribute("id").Value));
			var nCount = nodes.Count();
			if (startingTransaction == 0)
			{
				var initialDate = long.Parse(nodes.First().Attribute("time").Value) - 60;
				var defaultGitUserName = ConfigurationManager.AppSettings.Get("DefaultGitUserName");
				var defaultGitUser = _gitUsers.SingleOrDefault(u => u.Name.Equals(defaultGitUserName, StringComparison.OrdinalIgnoreCase));
				if (defaultGitUser == null)
					throw new ApplicationException("Cannot initialize new repository without a DefaultGitUserName specified!");
				execGitRaw("init", workingDir);
				execGitRaw("add --all", workingDir);
				execGitCommit(string.Format("commit --date={0} --author={1} -m \"Initial git commit.\"", initialDate, defaultGitUser), workingDir, initialDate.ToString(), defaultGitUser);
				execGitRaw("checkout -b dev", workingDir, true);
			}
			foreach (var transaction in nodes)
			{
				n++;
				var transactionId = Convert.ToInt32(transaction.Attribute("id").Value);
				Console.Write("Loading transaction {0} of {1} [id={2}]\x0D", n.ToString("00000"), nCount, transactionId.ToString("00000"));
				loadTransaction(depotName, transactionId, workingDir, transaction);
			}
// ReSharper restore PossibleNullReferenceException
			Console.WriteLine();
		}

		static void loadTransaction(string depotName, int transactionId, string workingDir, XElement transaction)
		{
// ReSharper disable PossibleNullReferenceException
			var accurevUser = transaction.Attribute("user").Value;
			var gitUser = translateUser(accurevUser);
			var unixDate = long.Parse(transaction.Attribute("time").Value);
// ReSharper restore PossibleNullReferenceException
			var commentNode = transaction.Descendants("comment").FirstOrDefault();
			var comment = (commentNode == null ? string.Empty : commentNode.Value);
			comment = string.IsNullOrEmpty(comment) ? "[no original comment]" : comment;
			comment += string.Format("{0}{0}[AccuRev Transaction #{1}]", Environment.NewLine, transactionId);
			var commentFile = string.Format(".\\_{0}_Comment.txt", depotName);
			var commentFilePath = Path.GetFullPath(commentFile);
			File.WriteAllText(commentFile, comment);
			execClean(workingDir);
			execAccuRev(string.Format("pop -R -O -v {0}_dev -L . -t {1} .", depotName, transactionId), workingDir);
			execGitRaw("add --all", workingDir);
			execGitCommit(string.Format("commit --date={0} --author={1} --file={2}", unixDate, gitUser, commentFilePath), workingDir, unixDate.ToString(), gitUser);
		}

		static GitUser translateUser(string accurevUser)
		{
			return _gitUsers.SingleOrDefault(u => u.AccuRevUserName.Equals(accurevUser, StringComparison.OrdinalIgnoreCase));
		}

		static void execClean(string workingDir)
		{
			foreach (var dir in Directory.GetDirectories(workingDir).Where(dir => !dir.EndsWith(".git")))
				Directory.Delete(dir, true);
			foreach (var file in Directory.GetFiles(workingDir).Where(file => !file.EndsWith(".gitignore")))
				File.Delete(file);
		}

		static string execAccuRev(string arguments, string workingDir)
		{
			var accuRevPath = ConfigurationManager.AppSettings["AccuRevPath"];
			var process = new Process
			{
				StartInfo = new ProcessStartInfo(accuRevPath, arguments)
			};
			if (!string.IsNullOrEmpty(workingDir))
				process.StartInfo.WorkingDirectory = workingDir;
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.StartInfo.RedirectStandardError = true;
			process.StartInfo.RedirectStandardOutput = true;
			if (!process.StartInfo.EnvironmentVariables.ContainsKey("ACCUREV_PRINCIPAL"))
				process.StartInfo.EnvironmentVariables.Add("ACCUREV_PRINCIPAL", "Ryan");
			process.Start();
			var result = process.StandardOutput.ReadToEnd();
			if (process.StandardError.EndOfStream == false)
			{
				var errors = process.StandardError.ReadToEnd();
				if (!errors.Contains("is defunct") && !errors.Contains("No element named") && !errors.Contains("Unable to proceed with annotate.") && !errors.Contains("Specified version not found for:"))
				{
					Debug.WriteLine(errors);
					throw new ApplicationException(string.Format("AccuRev has returned an error: {0}", errors));
				}
			}
			return result;
		}

		static void execGitRaw(string arguments, string workingDir, bool ignoreErrors = false)
		{
			var gitPath = ConfigurationManager.AppSettings["GitPath"];
			var process = new Process
			{
				StartInfo = new ProcessStartInfo(gitPath, arguments)
			};
			if (!string.IsNullOrEmpty(workingDir))
				process.StartInfo.WorkingDirectory = workingDir;
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.StartInfo.RedirectStandardError = true;
			process.StartInfo.RedirectStandardOutput = true;
			process.Start();
			process.StandardOutput.ReadToEnd();
			if (!ignoreErrors && process.StandardError.EndOfStream == false)
			{
				var errors = process.StandardError.ReadToEnd();
				throw new ApplicationException(string.Format("Git has returned an error: {0}", errors));
			}
		}

		static void execGitCommit(string arguments, string workingDir, string commitDate, GitUser gitUser)
		{
			var gitPath = ConfigurationManager.AppSettings["GitPath"];
			var process = new Process
			{
				StartInfo = new ProcessStartInfo(gitPath, arguments)
			};
			if (!string.IsNullOrEmpty(workingDir))
				process.StartInfo.WorkingDirectory = workingDir;
			process.StartInfo.EnvironmentVariables.Add("GIT_COMMITTER_DATE", commitDate);
			process.StartInfo.EnvironmentVariables.Add("GIT_COMMITTER_NAME", gitUser.Name);
			process.StartInfo.EnvironmentVariables.Add("GIT_COMMITTER_EMAIL", gitUser.Email);
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.StartInfo.RedirectStandardError = true;
			process.StartInfo.RedirectStandardOutput = true;
			process.Start();
			process.StandardOutput.ReadToEnd();
			if (process.StandardError.EndOfStream == false)
			{
				var errors = process.StandardError.ReadToEnd();
				throw new ApplicationException(string.Format("Git has returned an error: {0}", errors));
			}
		}

		internal class GitUser
		{
			public string Name { get; set; }
			public string Email { get; set; }
			public string AccuRevUserName { get; set; }

			public GitUser(string accuRevUserName, string name, string email)
			{
				Name = name;
				Email = email;
				AccuRevUserName = accuRevUserName;
			}

			public override string ToString()
			{
				return string.Format("\"{0} <{1}>\"", Name, Email);
			}
		}
	}
}
