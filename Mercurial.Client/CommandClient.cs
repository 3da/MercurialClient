// 
//  CommandClient.cs
//  
//  Author:
//       Levi Bard <levi@unity3d.com>
//  
//  Copyright (c) 2011 Levi Bard
// 
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Xml;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mercurial.Client
{
    /// <summary>
    /// Client class for the Merurial command server
    /// </summary>
    public class CommandClient : IDisposable
    {
        const string DefaultMercurialPath = "hg";
        const string MercurialEncodingKey = "HGENCODING";
        const int MercurialHeaderLength = 5;

        private Process _commandServer;

        /// <summary>
        /// The text encoding being used in the current session
        /// </summary>
        public Encoding Encoding { get; private set; }

        /// <summary>
        /// The set of capabilities supported by the command server
        /// </summary>
        public IEnumerable<string> Capabilities { get; private set; }

        /// <summary>
        /// The configuration of the current session
        /// </summary>
        /// <remarks>
        /// Equivalent to "key = value" from hgrc or `hg showconfig`
        /// </remarks>
        public IDictionary<string, string> Configuration
        {
            get
            {
                if (null != _configuration)
                    return _configuration;

                var result = GetCommandOutput(new[] { "showconfig" });
                if (0 != result.Result)
                    return null;

                return _configuration = ParseDictionary(result.Output, new[] { "=" });
            }
        }

        Dictionary<string, string> _configuration;

        /// <summary>
        /// The root directory of the current repository
        /// </summary>
        public string Root
        {
            get
            {
                return _root ?? (_root = GetCommandOutput(new[] { "root" }).Output.TrimEnd());
            }
        }

        string _root;

        public string Version
        {
            get
            {
                if (null != _version)
                    return _version;

                _version = GetCommandOutput(new[] { "version" }).Output;
                Match match = versionRegex.Match(_version);
                if (null == match || !match.Success)
                    throw new CommandException(string.Format("Invalid version string: {0}", _version));
                return _version = string.Format("{0}.{1}.{2}{3}",
                                                 match.Groups["major"].Value,
                                                 match.Groups["minor"].Value,
                                                 match.Groups["trivial"].Success ? match.Groups["trivial"].Value : "0",
                                                 match.Groups["additional"].Success ? match.Groups["additional"].Value : string.Empty);
            }
        }
        string _version;
        static Regex versionRegex = new Regex(@"^[^\)]+\([^\d]+(?<major>\d)\.(?<minor>\d)((.(?<trivial>\d))|(?<additional>.*))\)", RegexOptions.Compiled);

        /// <summary>
        /// Launch a new command server
        /// </summary>
        /// <param name='path'>
        /// The path to the root of the repository to be used
        /// </param>
        /// <param name='encoding'>
        /// The text encoding to be used for the session
        /// </param>
        /// <param name='configs'>
        /// A configuration dictionary to be passed to the command server
        /// </param>
        public CommandClient(string path, string encoding = null, IDictionary<string, string> configs = null, string mercurialPath = null)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be empty", nameof(path));
            if (!Directory.Exists(path) || !Directory.Exists(Path.Combine(path, ".hg")))
                throw new ArgumentException(string.Format("{0} is not a valid mercurial repository", path), nameof(path));

            var arguments = new StringBuilder("serve --cmdserver pipe ");
            arguments.AppendFormat("--cwd {0} --repository {0} ", path);

            if (string.IsNullOrEmpty(mercurialPath))
                mercurialPath = DefaultMercurialPath;

            if (null != configs)
            {
                // build config string in key=value format
                arguments.AppendFormat("--config {0} ",
                    configs.Aggregate(new StringBuilder(),
                        (accumulator, pair) => accumulator.AppendFormat("{0}={1},", pair.Key, pair.Value),
                        accumulator => accumulator.ToString()
                ));
            }

            var info = new ProcessStartInfo(mercurialPath, arguments.ToString().Trim())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

#if NET451
            if (String.IsNullOrEmpty(encoding))
                info.EnvironmentVariables[MercurialEncodingKey] = encoding;
#else
            if (String.IsNullOrEmpty(encoding))
                info.Environment[MercurialEncodingKey] = encoding;
#endif
            info.Environment["LANG"] = "en_US";
            try
            {
                _commandServer = Process.Start(info);
            }
            catch (Exception ex)
            {
                throw new ServerException("Error launching mercurial command server", ex);
            }

            Handshake();
        }

        /// <summary>
        /// Create a new repository
        /// </summary>
        /// <remarks>
        /// Equivalent to `hg init`
        /// </remarks>
        /// <param name='destination'>
        /// The directory in which to create the repository
        /// </param>
        public static void Initialize(string destination, string mercurialPath)
        {
            if (string.IsNullOrEmpty(mercurialPath))
                mercurialPath = DefaultMercurialPath;

            var psi = new ProcessStartInfo(mercurialPath, string.Format("init {0}", destination))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var hg = Process.Start(psi);
            hg.WaitForExit(5000);
            if (!hg.HasExited || 0 != hg.ExitCode)
                throw new CommandException(string.Format("Error creating repository at {0}", destination));
        }

        /// <summary>
        /// Create a copy of an existing repository
        /// </summary>
        /// <param name='source'>
        /// The path to the repository to copy
        /// </param>
        /// <param name='destination'>
        /// The path to the local destination for the clone
        /// </param>
        /// <param name='updateWorkingCopy'>
        /// Create a local working copy
        /// </param>
        /// <param name='updateToRevision'>
        /// Update the working copy to this revision after cloning, 
        /// or null for tip
        /// </param>
        /// <param name='cloneToRevision'>
        /// Only clone up to this revision, 
        /// or null for all revisions
        /// </param>
        /// <param name='onlyCloneBranch'>
        /// Only clone this branch, or null for all branches
        /// </param>
        /// <param name='forcePullProtocol'>
        /// Force usage of the pull protocol for local clones
        /// </param>
        /// <param name='compressData'>
        /// Compress changesets for transfer
        /// </param>
        public static void Clone(string source, string destination, bool updateWorkingCopy = true, string updateToRevision = null, string cloneToRevision = null, string onlyCloneBranch = null, bool forcePullProtocol = false, bool compressData = true, string mercurialPath = null)
        {
            CloneInternal(source, destination, updateWorkingCopy, updateToRevision, cloneToRevision, onlyCloneBranch, forcePullProtocol, compressData, mercurialPath);
        }

        private static void CloneInternal(string source, string destination, bool updateWorkingCopy, string updateToRevision, string cloneToRevision, string onlyCloneBranch, bool forcePullProtocol, bool compressData, string mercurialPath)
        {
            if (string.IsNullOrEmpty(source))
                throw new ArgumentException("Source must not be empty.", "source");
            if (string.IsNullOrEmpty(mercurialPath))
                mercurialPath = DefaultMercurialPath;

            var arguments = new List<string>() { "clone" };
            AddArgumentIf(arguments, !updateWorkingCopy, "--noupdate");
            AddArgumentIf(arguments, forcePullProtocol, "--pull");
            AddArgumentIf(arguments, !compressData, "--uncompressed");

            AddNonemptyStringArgument(arguments, updateToRevision, "--updaterev");
            AddNonemptyStringArgument(arguments, cloneToRevision, "--rev");
            AddNonemptyStringArgument(arguments, onlyCloneBranch, "--branch");

            arguments.Add(source);
            AddArgumentIf(arguments, !string.IsNullOrEmpty(destination), destination);

            var psi = new ProcessStartInfo(mercurialPath, string.Join(" ", arguments.ToArray()))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var hg = Process.Start(psi);
            hg.WaitForExit();

            if (0 != hg.ExitCode)
                throw new CommandException(string.Format("Error cloning {0}: {1}", source, hg.StandardOutput.ReadToEnd() + hg.StandardError.ReadToEnd()));
        }

        /// <summary>
        /// Schedules files to be version controlled and added to the repository
        /// </summary>
        /// <param name='files'>
        /// The files to be added
        /// </param>
        public void Add(params string[] fileparams)
        {
            Add(files: fileparams);
        }

        /// <summary>
        /// Schedules files to be version controlled and added to the repository
        /// </summary>
        /// <param name='files'>
        /// The files to be added
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <param name='recurseSubRepositories'>
        /// Recurse into subrepositories
        /// </param>
        /// <param name='dryRun'>
        /// Check whether files can be successfully added, 
        /// without actually adding them
        /// </param>
        public void Add(IEnumerable<string> files, string includePattern = null, string excludePattern = null, bool recurseSubRepositories = false, bool dryRun = false)
        {
            files = files ?? new List<string>();
            var arguments = new List<string>() { "add" };
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            AddArgumentIf(arguments, recurseSubRepositories, "--subrepos");
            AddArgumentIf(arguments, dryRun, "--dry-run");

            arguments.AddRange(files);

            ThrowOnFail(GetCommandOutput(arguments), 0, string.Format("Error adding {0}", string.Join(" ", files)));
        }

        /// <summary>
        /// Show the status of files in the repository
        /// </summary>
        /// <param name='files'>
        /// Only show status for these files
        /// </param>
        /// <returns>
        /// A dictionary mapping each file to its Status
        /// </returns>
        public IDictionary<string, FileStatus> Status(params string[] fileparams)
        {
            return Status(files: fileparams);
        }

        /// <summary>
        /// Show the status of files in the repository
        /// </summary>
        /// <param name='files'>
        /// Only show status for these files
        /// </param>
        /// <param name='onlyFilesWithThisStatus'>
        /// Only show files with this status
        /// </param>
        /// <param name='showCopiedSources'>
        /// Show the sources of copied files
        /// </param>
        /// <param name='fromRevision'>
        /// Show status changes between the current revision and this revision
        /// </param>
        /// <param name='onlyRevision'>
        /// Only show status changes that occurred during this revision
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <param name='recurseSubRepositories'>
        /// Recurse into subrepositories
        /// </param>
        /// <returns>
        /// A dictionary mapping each file to its Status
        /// </returns>
        public IDictionary<string, FileStatus> Status(IEnumerable<string> files, bool quiet = false, FileStatus onlyFilesWithThisStatus = FileStatus.Default, bool showCopiedSources = false, string fromRevision = null, string onlyRevision = null, string includePattern = null, string excludePattern = null, bool recurseSubRepositories = false)
        {
            var arguments = new List<string>() { "status" };

            AddArgumentIf(arguments, quiet, "--quiet");
            if (FileStatus.Default != onlyFilesWithThisStatus)
            {
                arguments.Add(ArgumentForStatus(onlyFilesWithThisStatus));
            }
            AddArgumentIf(arguments, showCopiedSources, "--copies");
            AddNonemptyStringArgument(arguments, fromRevision, "--rev");
            AddNonemptyStringArgument(arguments, onlyRevision, "--change");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            AddArgumentIf(arguments, recurseSubRepositories, "--subrepos");

            if (null != files)
                arguments.AddRange(files);

            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, "Error retrieving status");

            return result.Output.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).Aggregate(new Dictionary<string, FileStatus>(), (dict, line) =>
              {
                  if (2 < line.Length)
                      dict[line.Substring(2)] = ParseStatus(line.Substring(0, 1));

                  return dict;
              });
        }

        /// <summary>
        /// Commit changes into the repository
        /// </summary>
        /// <param name='message'>
        /// Commit message
        /// </param>
        /// <param name='files'>
        /// Files to commit, empty set will commit all changes reported by Status
        /// </param>
        public void Commit(string message, params string[] fileparams)
        {
            Commit(message: message, files: fileparams);
        }

        /// <summary>
        /// Commit changes into the repository
        /// </summary>
        /// <param name='message'>
        /// Commit message
        /// </param>
        /// <param name='files'>
        /// Files to commit, empty set will commit all changes reported by Status
        /// </param>
        /// <param name='addAndRemoveUnknowns'>
        /// Mark new files as added and missing files as removed before committing
        /// </param>
        /// <param name='closeBranch'>
        /// Mark a branch as closed
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <param name='messageLog'>
        /// Read the commit message from this file
        /// </param>
        /// <param name='date'>
        /// Record this as the commit date
        /// </param>
        /// <param name='user'>
        /// Record this user as the committer
        /// </param>
        public void Commit(string message, IEnumerable<string> files, bool addAndRemoveUnknowns = false, bool closeBranch = false, string includePattern = null, string excludePattern = null, string messageLog = null, DateTime? date = null, string user = null)
        {
            var arguments = new List<string>() { "commit" };
            AddNonemptyStringArgument(arguments, message, "--message");
            AddArgumentIf(arguments, addAndRemoveUnknowns, "--addremove");
            AddArgumentIf(arguments, closeBranch, "--close-branch");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            AddNonemptyStringArgument(arguments, messageLog, "--logfile");
            AddNonemptyStringArgument(arguments, user, "--user");
            AddFormattedDateArgument(arguments, date, "--date");

            if (null != files)
                arguments.AddRange(files);

            var result = GetCommandOutput(arguments);
            if (1 != result.Result && 0 != result.Result)
            {
                ThrowOnFail(result, 0, "Error committing");
            }
        }

        /// <summary>
        /// Get the revision history of the repository
        /// </summary>
        /// <param name='revisionRange'>
        /// Log the specified revisions
        /// </param>
        /// <param name='files'>
        /// Only get history for these files
        /// </param>
        /// <returns>
        /// An ordered list of Revisions
        /// </returns>
        public IList<Revision> Log(string revisionRange, params string[] fileparams)
        {
            return Log(revisionRange: revisionRange, files: fileparams);
        }

        /// <summary>
        /// Get the revision history of the repository
        /// </summary>
        /// <param name='revisionRange'>
        /// Log the specified revisions
        /// </param>
        /// <param name='files'>
        /// Only get history for these files
        /// </param>
        /// <param name='followAcrossCopy'>
        /// Follow history across copies and renames
        /// </param>
        /// <param name='followFirstMergeParent'>
        /// Only follow the first parent of merge changesets
        /// </param>
        /// <param name='fromDate'>
        /// Log revisions beginning with this date (requires toDate)
        /// </param>
        /// <param name='toDate'>
        /// Log revisions ending with this date (requires fromDate)
        /// </param>
        /// <param name='showCopiedFiles'>
        /// Show copied files
        /// </param>
        /// <param name='searchText'>
        /// Search case-insensitively for this text
        /// </param>
        /// <param name='showRemoves'>
        /// Include revisions where files were removed
        /// </param>
        /// <param name='onlyMerges'>
        /// Only log merges
        /// </param>
        /// <param name='excludeMerges'>
        /// Don't log merges
        /// </param>
        /// <param name='user'>
        /// Only log revisions committed by this user
        /// </param>
        /// <param name='branch'>
        /// Only log changesets in this named branch
        /// </param>
        /// <param name='pruneRevisions'>
        /// Do not log this revision nor its ancestors
        /// </param>
        /// <param name='limit'>
        /// Only log this many changesets
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <returns>
        /// An ordered list of Revisions
        /// </returns>
        public IList<Revision> Log(string revisionRange, IEnumerable<string> files, bool followAcrossCopy = false, bool followFirstMergeParent = false, DateTime? fromDate = null, DateTime? toDate = null, bool showCopiedFiles = false, string searchText = null, bool showRemoves = false, bool onlyMerges = false, bool excludeMerges = false, string user = null, string branch = null, string pruneRevisions = null, int limit = 0, string includePattern = null, string excludePattern = null)
        {
            var arguments = new List<string>() { "log", "--style", "xml" };
            AddNonemptyStringArgument(arguments, revisionRange, "--rev");
            AddArgumentIf(arguments, followAcrossCopy, "--follow");
            AddArgumentIf(arguments, followFirstMergeParent, "--follow-first");
            AddArgumentIf(arguments, showCopiedFiles, "--copies");
            AddNonemptyStringArgument(arguments, searchText, "--keyword");
            AddArgumentIf(arguments, showRemoves, "--removed");
            AddArgumentIf(arguments, onlyMerges, "--only-merges");
            AddArgumentIf(arguments, excludeMerges, "--no-merges");
            AddNonemptyStringArgument(arguments, user, "--user");
            AddNonemptyStringArgument(arguments, branch, "--branch");
            AddNonemptyStringArgument(arguments, pruneRevisions, "--prune");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            if (0 < limit)
            {
                arguments.Add("--limit");
                arguments.Add(limit.ToString());
            }
            if (fromDate != null && toDate != null)
            {
                arguments.Add(string.Format("{0} to {1}",
                                              fromDate.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                                              toDate.Value.ToString("yyyy-MM-dd HH:mm:ss")));
            }
            if (null != files)
                arguments.AddRange(files);

            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, "Error getting log");

            // Console.WriteLine (result.Output);

            try
            {
                return Revision.ParseRevisionsFromLog(result.Output);
            }
            catch (XmlException ex)
            {
                throw new CommandException("Error getting log", ex);
            }
        }

        /// <summary>
        /// Show new changesets in another repository
        /// </summary>
        /// <param name="source">
        /// Check this repository for incoming changesets
        /// </param>
        /// <param name="toRevision">
        /// Check up to this revision
        /// </param>
        /// <param name="force">
        /// Check even if the remote repository is unrelated
        /// </param>
        /// <param name="showNewestFirst">
        /// Get the newest changesets first
        /// </param>
        /// <param name="bundleFile">
        /// Store downloaded changesets here
        /// </param>
        /// <param name="branch">
        /// Only check this branch
        /// </param>
        /// <param name="limit">
        /// Only retrieve this many changesets
        /// </param>
        /// <param name="showMerges">
        /// Show merges
        /// </param>
        /// <param name="recurseSubRepos">
        /// Recurse into subrepositories
        /// </param>
        /// <returns>
        /// An ordered list of revisions
        /// </returns>
        public IList<Revision> Incoming(string source = null, string toRevision = null, bool force = false, bool showNewestFirst = false, string bundleFile = null, string branch = null, int limit = 0, bool showMerges = true, bool recurseSubRepos = false)
        {
            var arguments = new List<string>() { "incoming", "--style", "xml" };
            AddNonemptyStringArgument(arguments, toRevision, "--rev");
            AddArgumentIf(arguments, force, "--force");
            AddArgumentIf(arguments, showNewestFirst, "--newest-first");
            AddNonemptyStringArgument(arguments, bundleFile, "--bundle");
            AddNonemptyStringArgument(arguments, branch, "--branch");
            AddArgumentIf(arguments, !showMerges, "--no-merges");
            AddArgumentIf(arguments, recurseSubRepos, "--subrepos");
            if (0 < limit)
            {
                arguments.Add("--limit");
                arguments.Add(limit.ToString());
            }
            AddArgumentIf(arguments, !string.IsNullOrEmpty(source), source);

            var result = GetCommandOutput(arguments);
            if (0 != result.Result && 1 != result.Result)
                throw new CommandException("Error getting incoming", result);

            try
            {
                return Revision.ParseRevisionsFromLog(result.Output);
            }
            catch (XmlException ex)
            {
                throw new CommandException("Error getting incoming", ex);
            }
        }

        /// <summary>
        /// Show new changesets in this repository
        /// </summary>
        /// <param name="source">
        /// Check this repository for outgoing changesets
        /// </param>
        /// <param name="toRevision">
        /// Check up to this revision
        /// </param>
        /// <param name="force">
        /// Check even if the remote repository is unrelated
        /// </param>
        /// <param name="showNewestFirst">
        /// Get the newest changesets first
        /// </param>
        /// <param name="branch">
        /// Only check this branch
        /// </param>
        /// <param name="limit">
        /// Only retrieve this many changesets
        /// </param>
        /// <param name="showMerges">
        /// Show merges
        /// </param>
        /// <param name="recurseSubRepos">
        /// Recurse into subrepositories
        /// </param>
        /// <returns>
        /// An ordered list of revisions
        /// </returns>
        public IList<Revision> Outgoing(string source, string toRevision, bool force = false, bool showNewestFirst = false, string branch = null, int limit = 0, bool showMerges = true, bool recurseSubRepos = false)
        {
            var arguments = new List<string>() { "outgoing", "--style", "xml" };
            AddNonemptyStringArgument(arguments, toRevision, "--rev");
            AddArgumentIf(arguments, force, "--force");
            AddArgumentIf(arguments, showNewestFirst, "--newest-first");
            AddNonemptyStringArgument(arguments, branch, "--branch");
            AddArgumentIf(arguments, !showMerges, "--no-merges");
            AddArgumentIf(arguments, recurseSubRepos, "--subrepos");
            if (0 < limit)
            {
                arguments.Add("--limit");
                arguments.Add(limit.ToString());
            }
            AddArgumentIf(arguments, !string.IsNullOrEmpty(source), source);

            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, "Error getting incoming");

            try
            {
                return Revision.ParseRevisionsFromLog(result.Output);
            }
            catch (XmlException ex)
            {
                throw new CommandException("Error getting incoming", ex);
            }
        }

        /// <summary>
        /// Get heads
        /// </summary>
        /// <param name='revisions'>
        /// If specified, only branch heads associated with these changesets will be returned
        /// </param>
        /// <returns>
        /// A set of Revisions representing the heads
        /// </returns>
        public IEnumerable<Revision> Heads(params string[] revisionParams)
        {
            return Heads(revisions: revisionParams);
        }

        /// <summary>
        /// Get heads
        /// </summary>
        /// <param name='revisions'>
        /// If specified, only branch heads associated with these changesets will be returned
        /// </param>
        /// <param name='startRevision'>
        /// Only get heads which are descendants of this revision
        /// </param>
        /// <param name='onlyTopologicalHeads'>
        /// Only get topological heads
        /// </param>
        /// <param name='showClosed'>
        /// Also get heads of closed branches
        /// </param>
        /// <returns>
        /// A set of Revisions representing the heads
        /// </returns>
        public IEnumerable<Revision> Heads(IEnumerable<string> revisions, string startRevision = null, bool onlyTopologicalHeads = false, bool showClosed = false)
        {
            var arguments = new List<string>() { "heads", "--style", "xml" };
            AddNonemptyStringArgument(arguments, startRevision, "--rev");
            AddArgumentIf(arguments, onlyTopologicalHeads, "--topo");
            AddArgumentIf(arguments, showClosed, "--closed");
            if (null != revisions)
                arguments.AddRange(revisions);

            var result = GetCommandOutput(arguments);
            if (1 != result.Result && 0 != result.Result)
            {
                ThrowOnFail(result, 0, "Error getting heads");
            }

            try
            {
                return Revision.ParseRevisionsFromLog(result.Output);
            }
            catch (XmlException ex)
            {
                throw new CommandException("Error getting heads", ex);
            }
        }

        /// <summary>
        /// Get line-specific changeset information
        /// </summary>
        /// <param name='revision'>
        /// Annotate this revision
        /// </param>
        /// <param name='files'>
        /// Annotate these files
        /// </param>
        /// <returns>
        /// Raw annotation data
        /// </returns>
        public string Annotate(string revision, params string[] fileParams)
        {
            return Annotate(revision: revision, files: fileParams);
        }

        /// <summary>
        /// Get line-specific changeset information
        /// </summary>
        /// <param name='revision'>
        /// Annotate this revision
        /// </param>
        /// <param name='files'>
        /// Annotate these files
        /// </param>
        /// <param name='followCopies'>
        /// Follow copies and renames
        /// </param>
        /// <param name='annotateBinaries'>
        /// Annotate all files as though they were text
        /// </param>
        /// <param name='showAuthor'>
        /// List the author
        /// </param>
        /// <param name='showFilename'>
        /// List the filename
        /// </param>
        /// <param name='showDate'>
        /// List the date
        /// </param>
        /// <param name='showRevision'>
        /// List the revision number
        /// </param>
        /// <param name='showChangeset'>
        /// List the changeset ID (hash)
        /// </param>
        /// <param name='showLine'>
        /// List the line number
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <returns>
        /// Raw annotation data
        /// </returns>
        public string Annotate(string revision, IEnumerable<string> files, bool followCopies = true, bool annotateBinaries = false, bool showAuthor = true, bool showFilename = false, bool showDate = false, bool showRevision = true, bool showChangeset = false, bool showLine = false, bool shortDate = false, string includePattern = null, string excludePattern = null)
        {
            List<string> arguments = new List<string>() { "annotate" };

            AddNonemptyStringArgument(arguments, revision, "--rev");
            AddArgumentIf(arguments, !followCopies, "--no-follow");
            AddArgumentIf(arguments, annotateBinaries, "--text");
            AddArgumentIf(arguments, showAuthor, "--user");
            AddArgumentIf(arguments, showFilename, "--file");
            AddArgumentIf(arguments, showDate, "--date");
            AddArgumentIf(arguments, shortDate, "--quiet");
            AddArgumentIf(arguments, showRevision, "--number");
            AddArgumentIf(arguments, showChangeset, "--changeset");
            AddArgumentIf(arguments, showLine, "--line-number");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");

            if (null != files)
                arguments.AddRange(files);

            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, "Error annotating");

            return result.Output;
        }

        /// <summary>
        /// Get differences between revisions
        /// </summary>
        /// <param name='revision'>
        /// Get changes from this revision
        /// </param>
        /// <param name='files'>
        /// Get changes for these files
        /// </param>
        /// <returns>
        /// A unified diff
        /// </returns>
        public string Diff(string revision, params string[] fileParams)
        {
            return Diff(revision: revision, files: fileParams);
        }

        /// <summary>
        /// Get differences between revisions
        /// </summary>
        /// <param name='revision'>
        /// Get changes from this revision
        /// </param>
        /// <param name='files'>
        /// Get changes for these files
        /// </param>
        /// <param name='changeset'>
        /// Only get changes introduced by this changeset
        /// </param>
        /// <param name='diffBinaries'>
        /// Diff all files as though they were text
        /// </param>
        /// <param name='useGitFormat'>
        /// Use git-style extended diff format
        /// </param>
        /// <param name='showDates'>
        /// Show dates in diff headers
        /// </param>
        /// <param name='showFunctionNames'>
        /// Show the function name for each change
        /// </param>
        /// <param name='reverse'>
        /// Create a reverse diff
        /// </param>
        /// <param name='ignoreWhitespace'>
        /// Ignore all whitespace
        /// </param>
        /// <param name='ignoreWhitespaceOnlyChanges'>
        /// Ignore changes in the amount of whitespace
        /// </param>
        /// <param name='ignoreBlankLines'>
        /// Ignore changes whose lines are all blank
        /// </param>
        /// <param name='contextLines'>
        /// Use this many lines of context
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <param name='recurseSubRepositories'>
        /// Recurse into subrepositories
        /// </param>
        /// <returns>
        /// A unified diff
        /// </returns>
        public string Diff(string revision, IEnumerable<string> files, string changeset = null, bool diffBinaries = false, bool useGitFormat = false, bool showDates = true, bool showFunctionNames = false, bool reverse = false, bool ignoreWhitespace = false, bool ignoreWhitespaceOnlyChanges = false, bool ignoreBlankLines = false, int contextLines = 0, string includePattern = null, string excludePattern = null, bool recurseSubRepositories = false, bool stat = false)
        {
            var arguments = new List<string>() { "diff" };
            AddNonemptyStringArgument(arguments, revision, "--rev");
            AddNonemptyStringArgument(arguments, changeset, "--change");
            AddArgumentIf(arguments, diffBinaries, "--text");
            AddArgumentIf(arguments, useGitFormat, "--git");
            AddArgumentIf(arguments, !showDates, "--nodates");
            AddArgumentIf(arguments, showFunctionNames, "--show-function");
            AddArgumentIf(arguments, reverse, "--reverse");
            AddArgumentIf(arguments, ignoreWhitespace, "--ignore-all-space");
            AddArgumentIf(arguments, ignoreWhitespaceOnlyChanges, "--ignore-space-change");
            AddArgumentIf(arguments, ignoreBlankLines, "--ignore-blank-lines");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            AddArgumentIf(arguments, recurseSubRepositories, "--subrepos");
            AddArgumentIf(arguments, stat, "--stat");
            if (0 < contextLines)
            {
                arguments.Add("--unified");
                arguments.Add(contextLines.ToString());
            }

            if (null != files) arguments.AddRange(files);

            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, "Error getting diff");

            return result.Output;
        }

        /// <summary>
        /// Export the header and diffs for one or more changesets
        /// </summary>
        /// <param name='revisions'>
        /// Export these revisions
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when revisions is empty
        /// </exception>
        /// <returns>
        /// The output of the export
        /// </returns>
        public string Export(params string[] revisionParams)
        {
            return Export(revisions: revisionParams);
        }

        /// <summary>
        /// Export the header and diffs for one or more changesets
        /// </summary>
        /// <param name='revisions'>
        /// Export these revisions
        /// </param>
        /// <param name='outputFile'>
        /// Export output to a file with this formatted name
        /// </param>
        /// <param name='switchParent'>
        /// Diff against the second parent, instead of the first
        /// </param>
        /// <param name='diffBinaries'>
        /// Diff all files as though they were text
        /// </param>
        /// <param name='useGitFormat'>
        /// Use git-style extended diff format
        /// </param>
        /// <param name='showDates'>
        /// Show dates in diff headers
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when revisions is empty
        /// </exception>
        /// <returns>
        /// The output of the export
        /// </returns>
        public string Export(IEnumerable<string> revisions, string outputFile = null, bool switchParent = false, bool diffBinaries = false, bool useGitFormat = false, bool showDates = true)
        {
            if (null == revisions || 0 == revisions.Count())
                throw new ArgumentException("Revision list cannot be empty", "revisions");

            var arguments = new List<string>() { "export" };
            AddNonemptyStringArgument(arguments, outputFile, "--output");
            AddArgumentIf(arguments, switchParent, "--switch-parent");
            AddArgumentIf(arguments, diffBinaries, "--text");
            AddArgumentIf(arguments, useGitFormat, "--git");
            AddArgumentIf(arguments, !showDates, "--nodates");
            arguments.AddRange(revisions);

            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, string.Format("Error exporting {0}", string.Join(",", revisions.ToArray())));

            return result.Output;
        }

        /// <summary>
        /// Mark the specified files so that they will no longer be tracked after the next commit
        /// </summary>
        /// <param name='files'>
        /// Forget these files
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when an empty file list is passed
        /// </exception>
        public void Forget(params string[] fileParams)
        {
            Forget(files: fileParams);
        }

        /// <summary>
        /// Mark the specified files so that they will no longer be tracked after the next commit
        /// </summary>
        /// <param name='files'>
        /// Forget these files
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when an empty file list is passed
        /// </exception>
        public void Forget(IEnumerable<string> files, string includePattern = null, string excludePattern = null)
        {
            if (null == files || 0 == files.Count())
                throw new ArgumentException("File list cannot be empty", "files");

            var arguments = new List<string>() { "forget" };
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            arguments.AddRange(files);

            ThrowOnFail(GetCommandOutput(arguments), 0, string.Format("Error forgetting {0}", string.Join(",", files.ToArray())));
        }

        /// <summary>
        /// Merge the working copy with another revision
        /// </summary>
        /// <param name='revision'>
        /// Merge with this revision
        /// </param>
        /// <param name='force'>
        /// Force a merge, even though the working copy has uncommitted changes
        /// </param>
        /// <param name='mergeTool'>
        /// Use this merge tool
        /// </param>
        /// <param name='dryRun'>
        /// Attempt merge without actually merging
        /// </param>
        /// <returns>
        /// true if the merge succeeded with no unresolved files, 
        /// false if there are unresolved files
        /// </returns>
        public bool Merge(string revision, bool force = false, string mergeTool = null, bool dryRun = false)
        {
            var arguments = new List<string>() { "merge" };
            AddArgumentIf(arguments, force, "--force");
            AddNonemptyStringArgument(arguments, mergeTool, "--tool");
            AddArgumentIf(arguments, dryRun, "--preview");
            AddArgumentIf(arguments, !string.IsNullOrEmpty(revision), revision);

            var result = GetCommandOutput(arguments);
            if (0 != result.Result && 1 != result.Result)
            {
                ThrowOnFail(result, 0, "Error merging");
            }

            return (0 == result.Result);
        }

        /// <summary>
        /// Pull changes from another repository
        /// </summary>
        /// <param name='source'>
        /// Pull changes from this repository
        /// </param>
        /// <param name='toRevision'>
        /// Pull changes up to this revision
        /// </param>
        /// <param name='update'>
        /// Update to new branch head
        /// </param>
        /// <param name='force'>
        /// Force pulling changes if source repository is unrelated
        /// </param>
        /// <param name='branch'>
        /// Only pull this branch
        /// </param>
        /// <returns>
        /// true if the pull succeeded with no unresolved files, 
        /// false if there are unresolved files
        /// </returns>
        public bool Pull(string source, string toRevision = null, bool update = false, bool force = false, string branch = null)
        {
            var arguments = new List<string>() { "pull" };
            AddNonemptyStringArgument(arguments, toRevision, "--rev");
            AddArgumentIf(arguments, update, "--update");
            AddArgumentIf(arguments, force, "--force");
            AddNonemptyStringArgument(arguments, branch, "--branch");
            AddArgumentIf(arguments, !string.IsNullOrEmpty(source), source);

            var result = GetCommandOutput(arguments);
            if (0 != result.Result && 1 != result.Result)
            {
                ThrowOnFail(result, 0, "Error pulling");
            }

            return (0 == result.Result);
        }

        /// <summary>
        /// Push changesets to another repository
        /// </summary>
        /// <param name='destination'>
        /// Push changes to this repository
        /// </param>
        /// <param name='toRevision'>
        /// Push up to this revision
        /// </param>
        /// <param name='force'>
        /// Force push
        /// </param>
        /// <param name='branch'>
        /// Push only this branch
        /// </param>
        /// <param name='allowNewBranch'>
        /// Allow new branches to be pushed
        /// </param>
        /// <returns>
        /// Whether any changesets were pushed
        /// </returns>
        public bool Push(string destination, string toRevision = null, bool force = false, string branch = null, bool allowNewBranch = false)
        {
            var arguments = new List<string>() { "push" };
            AddNonemptyStringArgument(arguments, toRevision, "--rev");
            AddArgumentIf(arguments, force, "--force");
            AddNonemptyStringArgument(arguments, branch, "--branch");
            AddArgumentIf(arguments, allowNewBranch, "--new-branch");
            AddArgumentIf(arguments, !string.IsNullOrEmpty(destination), destination);

            var result = GetCommandOutput(arguments);
            if (1 != result.Result && 0 != result.Result)
            {
                ThrowOnFail(result, 0, "Error pushing");
            }
            return 0 == result.Result;
        }

        /// <summary>
        /// Update the working copy
        /// </summary>
        /// <param name='revision'>
        /// Update to this revision, or tip if empty
        /// </param>
        /// <param name='discardUncommittedChanges'>
        /// Discard uncommitted changes
        /// </param>
        /// <param name='updateAcrossBranches'>
        /// Update across branches (if there are no uncommitted changes)
        /// </param>
        /// <param name='toDate'>
        /// Update to the tipmost revision matching this date
        /// </param>
        /// <returns>
        /// true if the update succeeded with no unresolved files, 
        /// false if there are unresolved files
        /// </returns>
        public bool Update(string revision, bool discardUncommittedChanges = false, bool updateAcrossBranches = false, DateTime? toDate = null)
        {
            var arguments = new List<string>() { "update" };
            AddArgumentIf(arguments, discardUncommittedChanges, "--clean");
            AddArgumentIf(arguments, updateAcrossBranches, "--check");
            AddFormattedDateArgument(arguments, toDate, "--date");
            AddArgumentIf(arguments, !string.IsNullOrEmpty(revision), revision);

            var result = GetCommandOutput(arguments);
            if (0 != result.Result && 1 != result.Result)
            {
                ThrowOnFail(result, 0, "Error updating");
            }

            return (0 == result.Result);
        }

        /// <summary>
        /// Summarize the state of the working copy
        /// </summary>
        /// <param name='remote'>
        /// Check for incoming and outgoing changes on the default paths
        /// </param>
        /// <returns>
        /// The summary text
        /// </returns>
        public string Summary(bool remote)
        {
            var arguments = new List<string>() { "summary" };
            AddArgumentIf(arguments, remote, "--remote");
            var result = GetCommandOutput(arguments);
            ThrowOnFail(result, 0, "Error getting summary");
            return result.Output;
        }

        /// <summary>
        /// Restore files to an earlier state
        /// </summary>
        /// <param name='revision'>
        /// Revert to this revision
        /// </param>
        /// <param name='files'>
        /// Revert these files
        /// </param>
        public void Revert(string revision, params string[] fileParams)
        {
            Revert(revision: revision, files: fileParams);
        }

        /// <summary>
        /// Restore files to an earlier state
        /// </summary>
        /// <param name='revision'>
        /// Revert to this revision
        /// </param>
        /// <param name='files'>
        /// Revert these files
        /// </param>
        /// <param name='date'>
        /// Revert to the tipmost revision matching this date
        /// </param>
        /// <param name='saveBackups'>
        /// Save backup copies of reverted files
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <param name='dryRun'>
        /// Attempt revert without actually reverting
        /// </param>
        public void Revert(string revision, IEnumerable<string> files, DateTime? date = null, bool saveBackups = true, string includePattern = null, string excludePattern = null, bool dryRun = false)
        {
            var arguments = new List<string>() { "revert" };
            AddNonemptyStringArgument(arguments, revision, "--rev");
            AddFormattedDateArgument(arguments, date, "--date");
            AddArgumentIf(arguments, !saveBackups, "--no-backup");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            AddArgumentIf(arguments, dryRun, "--dry-run");

            if (null == files || 0 == files.Count())
            {
                arguments.Add("--all");
            }
            else
            {
                arguments.AddRange(files);
            }

            ThrowOnFail(GetCommandOutput(arguments), 0, "Error reverting");
        }

        /// <summary>
        /// Rename a file
        /// </summary>
        /// <param name='oldFileName'>
        /// The old (or existing) file name
        /// </param>
        /// <param name='newFileName'>
        /// The new file name
        /// </param>
        /// <param name='force'>
        /// Force the rename
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <param name='dryRun'>
        /// Attempt rename without actually renaming
        /// </param>
        public void Rename(string oldFileName, string newFileName, bool force = false, string includePattern = null, string excludePattern = null, bool dryRun = false)
        {
            var arguments = new List<string>() { "rename" };
            AddArgumentIf(arguments, force, "--force");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            AddArgumentIf(arguments, dryRun, "--dry-run");
            arguments.Add(oldFileName);
            arguments.Add(newFileName);

            ThrowOnFail(GetCommandOutput(arguments), 0, "Error renaming");
        }

        /// <summary>
        /// Get the text of a set of files
        /// </summary>
        /// <param name='revision'>
        /// Get text at the given revision
        /// </param>
        /// <param name='files'>
        /// Get text for these files
        /// </param>
        public IDictionary<string, string> Cat(string revision, params string[] fileParams)
        {
            return Cat(revision: revision, files: fileParams);
        }

        /// <summary>
        /// Get the text of a set of files
        /// </summary>
        /// <param name='revision'>
        /// Get text at the given revision
        /// </param>
        /// <param name='files'>
        /// Get text for these files
        /// </param>
        /// <param name='format'>
        /// Apply this format
        /// </param>
        /// <param name='decode'>
        /// Apply any matching decode filter
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        public IDictionary<string, string> Cat(string revision, IEnumerable<string> files, string format = null, bool decode = false, string includePattern = null, string excludePattern = null)
        {
            if (null == files || 0 == files.Count())
                throw new ArgumentException("File list cannot be empty", "files");

            var arguments = new List<string>() { "cat" };
            AddNonemptyStringArgument(arguments, revision, "--rev");
            AddNonemptyStringArgument(arguments, format, "--output");
            AddArgumentIf(arguments, decode, "--decode");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");

            var result = files.Aggregate(new Dictionary<string, string>(),
                (dict, file) =>
                {
                    var realArguments = new List<string>(arguments);
                    realArguments.Add(file);
                    dict[file] = GetCommandOutput(realArguments).Output;
                    return dict;
                });
            result.Count();
            return result;
        }

        /// <summary>
        /// Schedule files for removal from the repository
        /// </summary>
        /// <param name='files'>
        /// Schedule these files for removal
        /// </param>
        /// <param name='after'>
        /// Record the deletion of missing files
        /// </param>
        /// <param name='force'>
        /// Force removal of added and modified files
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when the file list is empty
        /// </exception>
        public void Remove(params string[] fileParams)
        {
            Remove(files: fileParams);
        }

        /// <summary>
        /// Schedule files for removal from the repository
        /// </summary>
        /// <param name='files'>
        /// Schedule these files for removal
        /// </param>
        /// <param name='after'>
        /// Record the deletion of missing files
        /// </param>
        /// <param name='force'>
        /// Force removal of added and modified files
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when the file list is empty
        /// </exception>
        public void Remove(IEnumerable<string> files, bool after = false, bool force = false, string includePattern = null, string excludePattern = null)
        {
            if (null == files || 0 == files.Count())
                throw new ArgumentException("File list cannot be empty", "files");

            var arguments = new List<string>() { "remove" };
            AddArgumentIf(arguments, after, "--after");
            AddArgumentIf(arguments, force, "--force");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            arguments.AddRange(files);

            ThrowOnFail(GetCommandOutput(arguments), 0, string.Format("Error removing {0}", string.Join(" , ", files)));
        }

        /// <summary>
        /// Set or check the merge status of files
        /// </summary>
        /// <param name='files'>
        /// Operate on these files
        /// </param>
        /// <param name='all'>
        /// Operate on all unresolved files
        /// </param>
        /// <param name='list'>
        /// Get list of files needing merge
        /// </param>
        /// <param name='mark'>
        /// Mark files as resolved
        /// </param>
        /// <param name='unmark'>
        /// Mark files as unresolved
        /// </param>
        /// <param name='mergeTool'>
        /// Use this merge tool
        /// </param>
        /// <param name='includePattern'>
        /// Include names matching the given patterns
        /// </param>
        /// <param name='excludePattern'>
        /// Exclude names matching the given patterns
        /// </param>
        public IDictionary<string, bool> Resolve(IEnumerable<string> files, bool all = false, bool list = false, bool mark = false, bool unmark = false, string mergeTool = null, string includePattern = null, string excludePattern = null)
        {
            var arguments = new List<string>() { "resolve" };
            AddArgumentIf(arguments, all, "--all");
            AddArgumentIf(arguments, list, "--list");
            AddArgumentIf(arguments, mark, "--mark");
            AddArgumentIf(arguments, unmark, "--unmark");
            AddNonemptyStringArgument(arguments, mergeTool, "--tool");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");
            if (null != files)
                arguments.AddRange(files);

            var result = GetCommandOutput(arguments);
            var statuses = result.Output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Aggregate(new Dictionary<string, bool>(),
                (dict, line) =>
                {
                    dict[line.Substring(2).Trim()] = (line[0] == 'R');
                    return dict;
                });
            statuses.Count();
            return statuses;
        }

        /// <summary>
        /// Get the parents of a revision
        /// </summary>
        /// <param name='file'>
        /// Get parents for the revision this file was last changed
        /// </param>
        /// <param name='revision'>
        /// Get parents for this revision
        /// </param>
        public IEnumerable<Revision> Parents(string file = null, string revision = null)
        {
            var arguments = new List<string>() { "parents", "--style", "xml" };
            AddNonemptyStringArgument(arguments, revision, "--rev");
            AddArgumentIf(arguments, !string.IsNullOrEmpty(file), file);

            var result = ThrowOnFail(GetCommandOutput(arguments), 0, "Error getting parents");
            return Revision.ParseRevisionsFromLog(result.Output);
        }

        public IDictionary<string, string> Paths(string name = null)
        {
            var arguments = new List<string>() { "paths" };
            AddArgumentIf(arguments, !string.IsNullOrEmpty(name), name);

            var result = ThrowOnFail(GetCommandOutput(arguments), 0, "Error getting paths");
            return result.Output.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries).Aggregate(new Dictionary<string, string>(), (dict, line) =>
            {
                var tokens = line.Split(new[] { '=' }, 2);
                dict[tokens[0].Trim()] = tokens[1].Trim();
                return dict;
            });
        }

        /// <summary>
        /// Rollback the last transaction (dangerous!)
        /// </summary>
        /// <param name='force'>
        /// Whether to ignore safety measures
        /// </param>
        /// <returns>
        /// Whether the rollback succeeded
        /// </returns>
        public bool Rollback(bool force = false)
        {
            var arguments = new List<string>() { "rollback" };
            AddArgumentIf(arguments, force, "--force");

            var result = GetCommandOutput(arguments);
            return (result.Result == 0);
        }

        private static readonly Dictionary<ArchiveType, string> ArchiveTypeToArgumentMapping = new Dictionary<ArchiveType, string>()
        {
            { ArchiveType.Default, string.Empty },
            { ArchiveType.Directory, "files" },
            { ArchiveType.Tar, "tar" },
            { ArchiveType.TarBzip2, "tbz2" },
            { ArchiveType.TarGzip, "tgz" },
            { ArchiveType.UncompressedZip, "uzip" },
            { ArchiveType.Zip, "zip" },
        };

        public void Archive(string destination, string revision = null, string prefix = null, ArchiveType type = ArchiveType.Default, bool decode = true, bool recurseSubRepositories = false, string includePattern = null, string excludePattern = null)
        {
            if (string.IsNullOrEmpty(destination))
                throw new ArgumentException("Destination cannot be empty", nameof(destination));

            var arguments = new List<string>() { "archive" };
            AddNonemptyStringArgument(arguments, revision, "--rev");
            AddNonemptyStringArgument(arguments, prefix, "--prefix");
            AddNonemptyStringArgument(arguments, ArchiveTypeToArgumentMapping[type], "--type");
            AddArgumentIf(arguments, !decode, "--no-decode");
            AddArgumentIf(arguments, recurseSubRepositories, "--subrepos");
            AddNonemptyStringArgument(arguments, includePattern, "--include");
            AddNonemptyStringArgument(arguments, excludePattern, "--exclude");

            arguments.Add(destination);

            ThrowOnFail(GetCommandOutput(arguments), 0, string.Format("Error archiving to {0}", destination));
        }

        #region Plumbing

        private void Handshake()
        {
            var handshake = ReadMessage();
            var headers = ParseDictionary(handshake.Message, new[] { ": " });

            if (!headers.ContainsKey("encoding") || !headers.ContainsKey("capabilities"))
                throw new ServerException("Error handshaking: expected 'encoding' and 'capabilities' fields");

            var encName = headers["encoding"];

            if (encName.StartsWith("cp"))
                Encoding = System.Text.Encoding.GetEncoding(int.Parse(encName.Substring(2)));
            else
                Encoding = Encoding.GetEncoding(encName);
            Capabilities = headers["capabilities"].Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
        }

        private CommandMessage ReadMessage()
        {
            byte[] header = new byte[MercurialHeaderLength];
            long bytesRead = 0;

            try
            {
                bytesRead = ReadAll(_commandServer.StandardOutput.BaseStream, header, 0, MercurialHeaderLength);
            }
            catch (Exception ex)
            {
                throw new ServerException("Error reading from command server", ex);
            }

            if (MercurialHeaderLength != bytesRead)
            {
                throw new ServerException(string.Format("Received malformed header from command server: {0} bytes", bytesRead));
            }

            var channel = CommandChannelFromFirstByte(header);
            var messageLength = (long)ReadUint(header, 1);

            if (CommandChannel.Input == channel || CommandChannel.Line == channel)
                return new CommandMessage(channel, messageLength.ToString());

            var messageBuffer = new byte[messageLength];

            try
            {
                if (messageLength > int.MaxValue)
                {
                    // .NET hates uints
                    int firstPart = (int)(messageLength / 2);
                    int secondPart = (int)(messageLength - firstPart);

                    bytesRead = ReadAll(_commandServer.StandardOutput.BaseStream, messageBuffer, 0, firstPart);
                    if (bytesRead == firstPart)
                        bytesRead += ReadAll(_commandServer.StandardOutput.BaseStream, messageBuffer, firstPart, secondPart);
                }
                else
                {
                    bytesRead = ReadAll(_commandServer.StandardOutput.BaseStream, messageBuffer, 0, (int)messageLength);
                }
            }
            catch (Exception ex)
            {
                throw new ServerException("Error reading from command server", ex);
            }

            if (bytesRead != messageLength)
                throw new ServerException(string.Format("Error reading from command server: Expected {0} bytes, read {1}", messageLength, bytesRead));

            CommandMessage message = new CommandMessage(CommandChannelFromFirstByte(header), messageBuffer);
            // Console.WriteLine ("READ: {0} {1}", message, message.Message);
            return message;
        }

        /// <summary>
        /// Reads all the requested bytes from a stream into a buffer
        /// </summary>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
        /// <param name='stream'>
        /// The stream to read.
        /// </param>
        /// <param name='buffer'>
        /// The buffer into which to read.
        /// </param>
        /// <param name='offset'>
        /// The beginning buffer offset to which to write.
        /// </param>
        /// <param name='length'>
        /// The number of bytes to read.
        /// </param>
        /// <exception cref='ArgumentNullException'>
        /// Is thrown when an argument passed to a method is invalid because it is <see langword="null" /> .
        /// </exception>
        static int ReadAll(Stream stream, byte[] buffer, int offset, int length)
        {
            if (null == stream)
                throw new ArgumentNullException(nameof(stream));

            int remaining = length;
            int read = 0;

            for (; remaining > 0; offset += read, remaining -= read)
            {
                read = stream.Read(buffer, offset, remaining);
            }

            return length - remaining;
        }

        private static readonly byte[] commandBuffer = System.Text.Encoding.UTF8.GetBytes("runcommand\n");

        /// <summary>
        /// Sends a command to the command server
        /// </summary>
        /// <remarks>
        /// You probably want GetCommandOutput instead
        /// </remarks>
        /// <param name='command'>
        /// A list of arguments, beginning with the command
        /// </param>
        /// <param name='outputs'>
        /// A dictionary mapping each relevant output channel to a stream which will capture its output
        /// </param>
        /// <param name='inputs'>
        /// A dictionary mapping each relevant input channel to a callback which will provide data on request
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when command is empty
        /// </exception>
        /// <returns>
        /// The return value of the command
        /// </returns>
        public int RunCommand(IList<string> command,
                               IDictionary<CommandChannel, Stream> outputs,
                               IDictionary<CommandChannel, Func<uint, byte[]>> inputs)
        {
            if (null == command || 0 == command.Count)
                throw new ArgumentException("Command must not be empty", nameof(command));

            var argumentBuffer = command.Aggregate(new List<byte>(), (bytes, arg) =>
            {
                bytes.AddRange(Encoding.GetBytes(arg));
                bytes.Add(0);
                return bytes;
            },
            bytes =>
            {
                bytes.RemoveAt(bytes.Count - 1);
                return bytes.ToArray();
            }
            ).ToArray();

            byte[] lengthBuffer = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(argumentBuffer.Length));

            lock (_commandServer)
            {
                _commandServer.StandardInput.BaseStream.Write(commandBuffer, 0, commandBuffer.Length);
                _commandServer.StandardInput.BaseStream.Write(lengthBuffer, 0, lengthBuffer.Length);
                _commandServer.StandardInput.BaseStream.Write(argumentBuffer, 0, argumentBuffer.Length);
                _commandServer.StandardInput.BaseStream.Flush();

                try
                {
                    while (true)
                    {
                        var message = ReadMessage();
                        if (CommandChannel.Result == message.Channel)
                            return ReadInt(message.Buffer, 0);

                        if (inputs != null && inputs.ContainsKey(message.Channel))
                        {
                            byte[] sendBuffer = inputs[message.Channel](ReadUint(message.Buffer, 0));
                        }

                        if (outputs != null && outputs.ContainsKey(message.Channel))
                        {
                            if (message.Buffer.Length > int.MaxValue)
                            {
                                // .NET hates uints
                                int firstPart = message.Buffer.Length / 2;
                                int secondPart = message.Buffer.Length - firstPart;
                                outputs[message.Channel].Write(message.Buffer, 0, firstPart);
                                outputs[message.Channel].Write(message.Buffer, firstPart, secondPart);
                            }
                            else
                            {
                                outputs[message.Channel].Write(message.Buffer, 0, message.Buffer.Length);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(string.Join(" ", command));
                    Console.WriteLine(ex);
                    _commandServer.StandardOutput.BaseStream.Flush();
                    _commandServer.StandardError.BaseStream.Flush();
                    throw;
                }
            }// lock commandServer
        }

        /// <summary>
        /// Sends a command to the command server and captures its output
        /// </summary>
        /// <param name='command'>
        /// A list of arguments, beginning with the command
        /// </param>
        /// <param name='inputs'>
        /// A dictionary mapping each relevant input channel to a callback which will provide data on request
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when command is empty
        /// </exception>
        /// <returns>
        /// A CommandResult containing the captured output and error streams
        /// </returns>
        public CommandResult GetCommandOutput(IList<string> command,
                                              IDictionary<CommandChannel, Func<uint, byte[]>> inputs = null)
        {
            var output = new MemoryStream();
            var error = new MemoryStream();
            var outputs = new Dictionary<CommandChannel, Stream>
            {
                { CommandChannel.Output, output },
                { CommandChannel.Error, error },
            };

            int result = RunCommand(command, outputs, inputs);
            return new CommandResult(output, error, result, Encoding);
        }

        void Close()
        {
            if (null != _commandServer)
                _commandServer.Dispose();
            _commandServer = null;
        }

        #region IDisposable implementation

        /// <summary>
        /// Releases all resources used by the <see cref="Mercurial.CommandClient"/> object.
        /// </summary>
        /// <remarks>
        /// Call <see cref="Dispose"/> when you are finished using the <see cref="Mercurial.CommandClient"/>. The
        /// <see cref="Dispose"/> method leaves the <see cref="Mercurial.CommandClient"/> in an unusable state. After calling
        /// <see cref="Dispose"/>, you must release all references to the <see cref="Mercurial.CommandClient"/> so the garbage
        /// collector can reclaim the memory that the <see cref="Mercurial.CommandClient"/> was occupying.
        /// </remarks>
        public void Dispose()
        {
            Close();
        }

        #endregion

        #endregion

        #region Utility

        /// <summary>
        /// Reads an int from a buffer in network byte order
        /// </summary>
        /// <param name='buffer'>
        /// Read from this buffer
        /// </param>
        /// <param name='offset'>
        /// Begin reading at this offset
        /// </param>
        /// <exception cref='ArgumentNullException'>
        /// Is thrown when buffer is null
        /// </exception>
        /// <exception cref='ArgumentOutOfRangeException'>
        /// Is thrown when buffer is not long enough to read an int, 
        /// beginning at offset
        /// </exception>
        /// <returns>
        /// The int
        /// </returns>
        internal static int ReadInt(byte[] buffer, int offset)
        {
            if (null == buffer) throw new ArgumentNullException("buffer");
            if (buffer.Length < offset + 4) throw new ArgumentOutOfRangeException("offset");

            return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buffer, offset));
        }

        /// <summary>
        /// Reads an unsigned int from a buffer in network byte order
        /// </summary>
        /// <param name='buffer'>
        /// Read from this buffer
        /// </param>
        /// <param name='offset'>
        /// Begin reading at this offset
        /// </param>
        /// <exception cref='ArgumentNullException'>
        /// Is thrown when buffer is null
        /// </exception>
        /// <exception cref='ArgumentOutOfRangeException'>
        /// Is thrown when buffer is not long enough to read an unsigned int, 
        /// beginning at offset
        /// </exception>
        /// <returns>
        /// The unsigned int
        /// </returns>
        internal static uint ReadUint(byte[] buffer, int offset)
        {
            if (null == buffer)
                throw new ArgumentNullException("buffer");
            if (buffer.Length < offset + 4)
                throw new ArgumentOutOfRangeException("offset");

            return (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buffer, offset));
        }

        /// <summary>
        /// Gets the CommandChannel represented by the first byte of a buffer
        /// </summary>
        /// <param name='header'>
        /// Read from this buffer
        /// </param>
        /// <exception cref='ArgumentException'>
        /// Is thrown when no valid CommandChannel is represented
        /// </exception>
        /// <returns>
        /// The CommandChannel
        /// </returns>
        internal static CommandChannel CommandChannelFromFirstByte(byte[] header)
        {
            char[] identifier = ASCIIEncoding.ASCII.GetChars(header, 0, 1);

            switch (identifier[0])
            {
                case 'I':
                    return CommandChannel.Input;
                case 'L':
                    return CommandChannel.Line;
                case 'o':
                    return CommandChannel.Output;
                case 'e':
                    return CommandChannel.Error;
                case 'r':
                    return CommandChannel.Result;
                case 'd':
                    return CommandChannel.Debug;
                default:
                    throw new ArgumentException(string.Format("Invalid channel identifier: {0}", identifier[0]), "header");
            }
        }

        /// <summary>
        /// Gets the byte representative of a CommandChannel
        /// </summary>
        internal static byte CommandChannelToByte(CommandChannel channel)
        {
            string identifier;

            switch (channel)
            {
                case CommandChannel.Debug:
                    identifier = "d";
                    break;
                case CommandChannel.Error:
                    identifier = "e";
                    break;
                case CommandChannel.Input:
                    identifier = "I";
                    break;
                case CommandChannel.Line:
                    identifier = "L";
                    break;
                case CommandChannel.Output:
                    identifier = "o";
                    break;
                case CommandChannel.Result:
                    identifier = "r";
                    break;
                default:
                    identifier = string.Empty;
                    break;
            }
            byte[] bytes = ASCIIEncoding.ASCII.GetBytes(identifier);
            return bytes[0];
        }

        /// <summary>
        /// Parses a delimited string into a dictionary
        /// </summary>
        /// <param name='input'>
        /// Parse this string
        /// </param>
        /// <param name='delimiters'>
        /// Split on these delimiters
        /// </param>
        /// <returns>
        /// The dictionary
        /// </returns>
        internal static Dictionary<string, string> ParseDictionary(string input, string[] delimiters)
        {
            Dictionary<string, string> headers = input.Split('\n')
                .Aggregate(new Dictionary<string, string>(),
                (dict, line) =>
                {
                    var tokens = line.Split(delimiters, 2, StringSplitOptions.None);
                    if (2 == tokens.Count())
                        dict[tokens[0]] = tokens[1];
                    return dict;
                });
            headers.Count();
            return headers;
        }


        /// <summary>
        /// Conditionally add a string to a collection
        /// </summary>
        /// <param name='arguments'>
        /// The collection
        /// </param>
        /// <param name='condition'>
        /// The condition
        /// </param>
        /// <param name='argument'>
        /// The argument to add
        /// </param>
        internal static void AddArgumentIf(ICollection<string> arguments, bool condition, string argument)
        {
            if (condition) arguments.Add(argument);
        }


        /// <summary>
        /// Conditionally add two strings to a collection
        /// </summary>
        /// <param name='arguments'>
        /// The collection
        /// </param>
        /// <param name='argument'>
        /// If this is not empty, add this, prefixed by argumentPrefix
        /// </param>
        /// <param name='argumentPrefix'>
        /// The prefix to be added
        /// </param>
        internal static void AddNonemptyStringArgument(ICollection<string> arguments, string argument, string argumentPrefix)
        {
            if (!string.IsNullOrEmpty(argument))
            {
                arguments.Add(argumentPrefix);
                arguments.Add(argument);
            }
        }

        /// <summary>
        /// Conditionally add a formatted date argument to a collection
        /// </summary>
        /// <param name='arguments'>
        /// The collection
        /// </param>
        /// <param name='date'>
        /// If this is not default(DateTime), add this, prefixed by datePrefix
        /// </param>
        /// <param name='datePrefix'>
        /// The prefix to be added
        /// </param>
        internal static void AddFormattedDateArgument(ICollection<string> arguments, DateTime? date, string datePrefix)
        {
            if (date != null)
            {
                arguments.Add(datePrefix);
                arguments.Add(date.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }
        }

        CommandResult ThrowOnFail(CommandResult result, int expectedResult, string failureMessage)
        {
            if (expectedResult != result.Result)
            {
                throw new CommandException(failureMessage, result);
            }
            return result;
        }

        /// <summary>
        /// Get a string argument representing a Status
        /// </summary>
        internal static string ArgumentForStatus(FileStatus status)
        {
            switch (status)
            {
                case FileStatus.Added:
                    return "--added";
                case FileStatus.Clean:
                    return "--clean";
                case FileStatus.Ignored:
                    return "--ignored";
                case FileStatus.Modified:
                    return "--modified";
                case FileStatus.Removed:
                    return "--removed";
                case FileStatus.Unknown:
                    return "--unknown";
                case FileStatus.Missing:
                    return "--deleted";
                case FileStatus.All:
                    return "--all";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Parse a status from its indicator text
        /// </summary>
        public static FileStatus ParseStatus(string input)
        {
            if (Enum.GetValues(typeof(FileStatus)).Cast<FileStatus>().Any(x => ((char)x) == input[0]))
                return (FileStatus)(input[0]);
            return FileStatus.Clean;
        }

        #endregion
    }
}

