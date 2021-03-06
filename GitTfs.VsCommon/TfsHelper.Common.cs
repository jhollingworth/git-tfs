using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.TeamFoundation.Server;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using SEP.Extensions;
using Sep.Git.Tfs.Core;
using Sep.Git.Tfs.Core.TfsInterop;
using StructureMap;

namespace Sep.Git.Tfs.VsCommon
{
    public abstract class TfsHelperBase : ITfsHelper
    {
        private readonly TextWriter _stdout;
        private string _url;
        private readonly TfsApiBridge _bridge;
        private readonly IContainer _container;

        public TfsHelperBase(TextWriter stdout, TfsApiBridge bridge, IContainer container)
        {
            _stdout = stdout;
            _bridge = bridge;
            _container = container;
        }

        public abstract string TfsClientLibraryVersion { get; }

        public string Url
        {
            get { return _url; }
            set { _url = value; UpdateServer(); }
        }

        protected abstract void UpdateServer();

        private string[] _legacyUrls;
        public string[] LegacyUrls
        {
            get { return _legacyUrls ?? (_legacyUrls = new string[0]); }
            set { _legacyUrls = value; }
        }

        protected abstract T GetService<T>();

        protected VersionControlServer VersionControl
        {
            get
            {
                var versionControlServer = GetService<VersionControlServer>();
                versionControlServer.NonFatalError += NonFatalError;
                return versionControlServer;
            }
        }

        private WorkItemStore WorkItems
        {
            get
            {
                return GetService<WorkItemStore>();
            }
        }

        private void NonFatalError(object sender, ExceptionEventArgs e)
        {
            _stdout.WriteLine(e.Failure.Message);
            Trace.WriteLine("Failure: " + e.Failure.Inspect(), "tfs non-fatal error");
            Trace.WriteLine("Exception: " + e.Exception.Inspect(), "tfs non-fatal error");
        }

        private IGroupSecurityService GroupSecurityService
        {
            get { return GetService<IGroupSecurityService>(); }
        }

        public IEnumerable<ITfsChangeset> GetChangesets(string path, long startVersion, GitTfsRemote remote)
        {
            var changesets = VersionControl.QueryHistory(path, VersionSpec.Latest, 0, RecursionType.Full,
                                                         null, new ChangesetVersionSpec((int)startVersion), VersionSpec.Latest, int.MaxValue, true,
                                                         true, true);
            return changesets.Cast<Changeset>()
                .OrderBy(changeset => changeset.ChangesetId)
                .Select(changeset => BuildTfsChangeset(changeset, remote));
        }

        private ITfsChangeset BuildTfsChangeset(Changeset changeset, GitTfsRemote remote)
        {
            var tfsChangeset = _container.With<ITfsHelper>(this).With<IChangeset>(_bridge.Wrap<WrapperForChangeset, Changeset>(changeset)).GetInstance<TfsChangeset>();
            tfsChangeset.Summary = new TfsChangesetInfo {ChangesetId = changeset.ChangesetId, Remote = remote};
            return tfsChangeset;
        }

        public void WithWorkspace(string localDirectory, IGitTfsRemote remote, TfsChangesetInfo versionToFetch, Action<ITfsWorkspace> action)
        {
            var workspace = GetWorkspace(localDirectory, remote.TfsRepositoryPath);
            try
            {
                var tfsWorkspace = _container.With("localDirectory").EqualTo(localDirectory)
                    .With("remote").EqualTo(remote)
                    .With("contextVersion").EqualTo(versionToFetch)
                    .With("workspace").EqualTo(_bridge.Wrap<WrapperForWorkspace, Workspace>(workspace))
                    .GetInstance<TfsWorkspace>();
                action(tfsWorkspace);
            }
            finally
            {
                workspace.Delete();
            }
        }

        private Workspace GetWorkspace(string localDirectory, string repositoryPath)
        {
            try
            {
                var workspace = VersionControl.CreateWorkspace(GenerateWorkspaceName());
                workspace.CreateMapping(new WorkingFolder(repositoryPath, localDirectory));
                return workspace;
            }
            catch (MappingConflictException e)
            {
                throw new GitTfsException(e.Message, new[] { "Run 'git tfs cleanup-workspace' to remove the workspace." }, e);
            }
        }

        private string GenerateWorkspaceName()
        {
            return "git-tfs-" + Guid.NewGuid();
        }

        public abstract long ShowCheckinDialog(IWorkspace workspace, IPendingChange[] pendingChanges, IEnumerable<IWorkItemCheckedInfo> checkedInfos, string checkinComment);

        public void CleanupWorkspaces(string workingDirectory)
        {
            Trace.WriteLine("Looking for workspaces mapped to @\"" + workingDirectory +"\"...", "cleanup-workspace");
            var workspace = VersionControl.TryGetWorkspace(workingDirectory);
            if(workspace != null)
            {
                Trace.WriteLine("Found mapping in workspace \"" + workspace.DisplayName + "\".", "cleanup-workspace");
                if (workspace.Folders.Length == 1)
                {
                    _stdout.WriteLine("Removing workspace \"" + workspace.DisplayName + "\".");
                    workspace.Delete();
                }
                else
                {
                    foreach (var mapping in workspace.Folders.Where(f => Path.GetFullPath(f.LocalItem).ToLower() == Path.GetFullPath(workingDirectory).ToLower()))
                    {
                        _stdout.WriteLine("Removing @\"" + mapping.LocalItem + "\" from workspace \"" + workspace.DisplayName + "\".");
                        workspace.DeleteMapping(mapping);
                    }

                }
            }
        }

        public NetworkCredential Credentials { get; set; }


        public bool HasShelveset(string shelvesetName)
        {
            var matchingShelvesets = VersionControl.QueryShelvesets(shelvesetName, GetAuthenticatedUser());
            return matchingShelvesets != null && matchingShelvesets.Length > 0;
        }

        protected abstract string GetAuthenticatedUser();

        public abstract bool CanShowCheckinDialog { get; }

        public IShelveset CreateShelveset(IWorkspace workspace, string shelvesetName)
        {
            var shelveset = new Shelveset(_bridge.Unwrap<Workspace>(workspace).VersionControlServer, shelvesetName, workspace.OwnerName);
            return _bridge.Wrap<WrapperForShelveset, Shelveset>(shelveset);
        }

        public IIdentity GetIdentity(string username)
        {
            return _bridge.Wrap<WrapperForIdentity, Identity>(GroupSecurityService.ReadIdentity(SearchFactor.AccountName, username, QueryMembership.None));
        }

        public ITfsChangeset GetLatestChangeset(GitTfsRemote remote)
        {
            var history = VersionControl.QueryHistory(remote.TfsRepositoryPath, VersionSpec.Latest, 0,
                                                      RecursionType.Full, null, null, VersionSpec.Latest, 1, true, false,
                                                      false);
            return BuildTfsChangeset(history.Cast<Changeset>().Single(), remote);
        }

        public IChangeset GetChangeset(int changesetId)
        {
            return _bridge.Wrap<WrapperForChangeset, Changeset>(VersionControl.GetChangeset(changesetId));
        }

        public ITfsChangeset GetChangeset(int changesetId, GitTfsRemote remote)
        {
            return BuildTfsChangeset(VersionControl.GetChangeset(changesetId), remote);
        }

        public bool MatchesUrl(string tfsUrl)
        {
            return Url == tfsUrl || LegacyUrls.Contains(tfsUrl);
        }

        public IEnumerable<IWorkItemCheckinInfo> GetWorkItemInfos(IEnumerable<string> workItems, TfsWorkItemCheckinAction checkinAction)
        {
            return
                GetWorkItemInfosHelper<IWorkItemCheckinInfo, WrapperForWorkItemCheckinInfo, WorkItemCheckinInfo>(
                    workItems, checkinAction, GetWorkItemInfo);
        }

        public IEnumerable<IWorkItemCheckedInfo> GetWorkItemCheckedInfos(IEnumerable<string> workItems, TfsWorkItemCheckinAction checkinAction)
        {
            return
                GetWorkItemInfosHelper<IWorkItemCheckedInfo, WrapperForWorkItemCheckedInfo, WorkItemCheckedInfo>(
                    workItems, checkinAction, GetWorkItemCheckedInfo);
        }

        private IEnumerable<TInterface> GetWorkItemInfosHelper<TInterface, TWrapper, TInstance>(IEnumerable<string> workItems, 
            TfsWorkItemCheckinAction checkinAction, Func<string, WorkItemCheckinAction, TInstance> func)
            where TWrapper : class
        {
            return (from workItem in workItems
                    select _bridge.Wrap<TWrapper, TInstance>(
                        func(workItem, _bridge.Convert<WorkItemCheckinAction>(checkinAction))))
                .Cast<TInterface>();
        }

        private WorkItemCheckinInfo GetWorkItemInfo(string workItem, WorkItemCheckinAction checkinAction)
        {
            return new WorkItemCheckinInfo(WorkItems.GetWorkItem(Convert.ToInt32(workItem)), checkinAction);
        }

        

        private static WorkItemCheckedInfo GetWorkItemCheckedInfo(string workitem, WorkItemCheckinAction checkinAction)
        {
            return new WorkItemCheckedInfo(Convert.ToInt32(workitem), true, checkinAction);
        }
    }
}