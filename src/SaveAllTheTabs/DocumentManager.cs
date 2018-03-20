using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Newtonsoft.Json;
using SaveAllTheTabs.Polyfills;

namespace SaveAllTheTabs
{
    internal interface IDocumentManager
    {
        event EventHandler GroupsReset;

        ObservableCollection<DocumentGroup> Groups { get; }

        int GroupCount { get; }
        bool HasSlotGroups { get; }

        DocumentGroup GetGroup(int slot);
        DocumentGroup GetGroup(string name);
        DocumentGroup GetSelectedGroup();

        void SaveGroup(string name, int? slot = null);

        void RestoreGroup(int slot);
        void RestoreGroup(DocumentGroup group);

        void OpenGroup(int slot);
        void OpenGroup(DocumentGroup group);

        void CloseGroup(DocumentGroup group);

        void MoveGroup(DocumentGroup group, int delta);

        void SetGroupSlot(DocumentGroup group, int slot);

        void RemoveGroup(DocumentGroup group, bool confirm = true);

        void SaveStashGroup();
        void RestoreStashGroup();
        void OpenStashGroup();

        bool HasStashGroup { get; }

        void ExportGroups(string filePath);
        void ImportGroups(string filePath);

        int? FindFreeSlot();
    }

    internal class DocumentManager : IDocumentManager
    {
        public event EventHandler GroupsReset;

        private const string UndoGroupName = "<undo>";
        private const string StashGroupName = "<stash>";

        private const int SlotMin = 1;
        private const int SlotMax = 9;

        private const string StorageCollectionPath = "SaveAllTheTabs";
        private const string SavedTabsStoragePropertyFormat = "SavedTabs.{0}";
        private const int StorageStringMaxLength = 512000;

        private SaveAllTheTabsPackage Package { get; }
        private IServiceProvider ServiceProvider => Package;
        private IVsUIShellDocumentWindowMgr DocumentWindowMgr { get; }
        //public IVsUIShellOpenDocument OpenDocument { get; }

        private string SolutionName => Package.Environment.Solution?.FullName;

        public ObservableCollection<DocumentGroup> Groups { get; private set; }

        public DocumentManager(SaveAllTheTabsPackage package)
        {
            Package = package;

            package.SolutionChanged += (sender, args) => LoadGroups();
            LoadGroups();

            DocumentWindowMgr = ServiceProvider.GetService(typeof(IVsUIShellDocumentWindowMgr)) as IVsUIShellDocumentWindowMgr;
            //OpenDocument = ServiceProvider.GetService(typeof(IVsUIShellOpenDocument)) as IVsUIShellOpenDocument;
        }

        private IDisposable _changeSubscription;

        private void LoadGroups()
        {
            _changeSubscription?.Dispose();

            // Load presets for the current solution
            Groups = new TrulyObservableCollection<DocumentGroup>(LoadGroupsForSolution());

            _changeSubscription = new CompositeDisposable
                                  {
                                      Observable.FromEventPattern<NotifyCollectionChangedEventArgs>(Groups, nameof(TrulyObservableCollection<DocumentGroup>.CollectionChanged))
                                                .Subscribe(re => SaveGroupsForSolution()),

                                      Observable.FromEventPattern<PropertyChangedEventArgs>(Groups, nameof(TrulyObservableCollection<DocumentGroup>.CollectionItemChanged))
                                                .Where(re => re.EventArgs.PropertyName == nameof(DocumentGroup.Name) ||
                                                             re.EventArgs.PropertyName == nameof(DocumentGroup.Files) ||
                                                             re.EventArgs.PropertyName == nameof(DocumentGroup.Positions) ||
                                                             re.EventArgs.PropertyName == nameof(DocumentGroup.Slot))
                                                .Throttle(TimeSpan.FromSeconds(1))
                                                .ObserveOnDispatcher()
                                                .Subscribe(re => SaveGroupsForSolution())
                                  };

            GroupsReset?.Invoke(this, EventArgs.Empty);
        }

        public int GroupCount => Groups?.Count ?? 0;
        public bool HasSlotGroups => Groups?.Any(g => g.Slot.HasValue) == true;

        public DocumentGroup GetGroup(int slot) => Groups.FindBySlot(slot);
        public DocumentGroup GetGroup(string name) => Groups.FindByName(name);
        public DocumentGroup GetSelectedGroup() => Groups.SingleOrDefault(g => g.IsSelected);

        public void SaveGroup(string name, int? slot = null)
        {
            if (DocumentWindowMgr == null)
            {
                Debug.Assert(false, "IVsUIShellDocumentWindowMgr", String.Empty, 0);
                return;
            }

            if (!Package.Environment.GetDocumentWindows().Any())
            {
                return;
            }

            var isBuiltIn = IsBuiltInGroup(name);
            if (isBuiltIn)
            {
                slot = null;
            }

            var group = Groups.FindByName(name);

            var files = new DocumentFilesHashSet(Package.Environment.GetDocumentFiles().OrderBy(Path.GetFileName));
            //var bps = Package.Environment.GetMatchingBreakpoints(files, StringComparer.OrdinalIgnoreCase));

            using (var stream = new VsOleStream())
            {
                var hr = DocumentWindowMgr.SaveDocumentWindowPositions(0, stream);
                if (hr != VSConstants.S_OK)
                {
                    Debug.Assert(false, "SaveDocumentWindowPositions", String.Empty, hr);

                    if (group != null)
                    {
                        Groups.Remove(group);
                    }
                    return;
                }
                stream.Seek(0, SeekOrigin.Begin);

                var documents = String.Join(", ", files.Select(Path.GetFileName));

                if (group == null)
                {
                    group = new DocumentGroup
                    {
                        Name = name,
                        Description = documents,
                        Files = files,
                        Positions = stream.ToArray()
                    };

                    TrySetSlot(group, slot);
                    if (isBuiltIn)
                    {
                        Groups.Insert(0, group);
                    }
                    else
                    {
                        Groups.Add(group);
                    }
                }
                else
                {
                    SaveUndoGroup(group);

                    group.Description = documents;
                    group.Files = files;
                    group.Positions = stream.ToArray();
                    Groups[Groups.IndexOf(group)] = group;

                    TrySetSlot(group, slot);
                }
            }
        }

        private void TrySetSlot(DocumentGroup group, int? slot)
        {
            if (!slot.HasValue || !(slot <= SlotMax) || group.Slot == slot)
            {
                return;
            }

            // Find out if there is an existing item in the desired slot
            var resident = Groups.FindBySlot((int)slot);
            if (resident != null)
            {
                resident.Slot = null;
            }

            group.Slot = slot;
        }

        public void RestoreGroup(int slot)
        {
            RestoreGroup(Groups.FindBySlot(slot));
        }

        public void RestoreGroup(DocumentGroup group)
        {
            if (group == null)
            {
                return;
            }

            var windows = Package.Environment.GetDocumentWindows();
            if (!IsUndoGroup(group.Name) && windows.Any())
            {
                SaveUndoGroup();
            }

            Package.Environment.Documents.CloseAll();

            OpenGroup(group);
        }

        public void OpenGroup(int slot)
        {
            OpenGroup(Groups.FindBySlot(slot));
        }

        public void OpenGroup(DocumentGroup group)
        {
            if (group == null)
            {
                return;
            }

            if (group.Positions != null)
            {
                using (var stream = new VsOleStream())
                {
                    stream.Write(group.Positions, 0, group.Positions.Length);
                    stream.Seek(0, SeekOrigin.Begin);

                    var hr = DocumentWindowMgr.ReopenDocumentWindows(stream);
                    if (hr != VSConstants.S_OK)
                    {
                        Debug.Assert(false, "ReopenDocumentWindows", String.Empty, hr);
                    }
                }
            }

            var openFiles = Package.Environment.GetDocumentFiles();
            foreach (var file in group.Files)
            {
                if (!openFiles.Any(f => string.Compare(f, file, true) == 0))
                {
                    OpenFile(file);
                }
            }
        }

        public void OpenFile(string path)
        {
            Microsoft.VisualStudio.Shell.VsShellUtilities.OpenDocument(ServiceProvider, path);
            //var viewId = Guid.Parse(LogicalViewID.Primary);
            //var hr = OpenDocument.OpenDocumentViaProject(path,
            //    ref viewId,
            //    out Microsoft.VisualStudio.OLE.Interop.IServiceProvider ppSP,
            //    out IVsUIHierarchy ppHier,
            //    out uint pitemid,
            //    out IVsWindowFrame ppWindowFrame);
            //if (hr != VSConstants.S_OK || ppWindowFrame == null)
            //{
            //    Debug.Assert(false, "OpenDocumentViaProject", "Path: \"{0}\"\nResult: {1}", path, hr);
            //}
        }

        public void CloseGroup(DocumentGroup group)
        {
            if (group?.Files == null)
            {
                return;
            }

            var documents = from d in Package.Environment.GetDocuments()
                            where @group.Files.Contains(d.FullName)
                            select d;
            documents.CloseAll();
        }

        public void MoveGroup(DocumentGroup group, int delta)
        {
            if (group == null || group.IsBuiltIn)
            {
                return;
            }

            var index = Groups.IndexOf(group);
            var newIndex = index + delta;
            if (newIndex < 0 || newIndex >= Groups.Count)
            {
                return;
            }

            var resident = Groups[newIndex];
            if (resident?.IsBuiltIn == true)
            {
                return;
            }

            Groups.Move(index, newIndex);
        }

        public void SetGroupSlot(DocumentGroup group, int slot)
        {
            if (group == null || group.Slot == slot)
            {
                return;
            }

            var resident = Groups.FindBySlot(slot);

            group.Slot = slot;

            if (resident != null)
            {
                resident.Slot = null;
            }
        }

        public void RemoveGroup(DocumentGroup group, bool confirm = true)
        {
            if (group == null)
            {
                return;
            }

            if (confirm)
            {
                var window = new ConfirmDeleteTabsWindow(group.Name);
                if (window.ShowDialog() == false)
                {
                    return;
                }
            }

            SaveUndoGroup(group);
            Groups.Remove(group);
        }

        private void SaveUndoGroup()
        {
            SaveGroup(UndoGroupName);
        }

        private void SaveUndoGroup(DocumentGroup group)
        {
            var undo = Groups.FindByName(UndoGroupName);

            if (undo == null)
            {
                undo = new DocumentGroup
                {
                    Name = UndoGroupName,
                    Description = group.Description,
                    Files = group.Files,
                    Positions = group.Positions
                };

                Groups.Insert(0, undo);
            }
            else
            {
                undo.Description = group.Description;
                undo.Files = group.Files;
                undo.Positions = group.Positions;
            }
        }

        public void SaveStashGroup()
        {
            SaveGroup(StashGroupName);
        }

        public void OpenStashGroup()
        {
            OpenGroup(Groups.FindByName(StashGroupName));
        }

        public void RestoreStashGroup()
        {
            RestoreGroup(Groups.FindByName(StashGroupName));
        }

        public bool HasStashGroup => Groups.FindByName(StashGroupName) != null;

        private class ExportData
        {
            public string SolutionName { get; set; }
            public List<DocumentGroup> Groups { get; set; }
        }

        public void ExportGroups(string filePath)
        {
            var export = new ExportData
            {
                SolutionName = SolutionName,
                Groups = Groups.ToList()
            };
            var json = JsonConvert.SerializeObject(export, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        public void ImportGroups(string filePath)
        {
            var json = File.ReadAllText(filePath);
            var import = JsonConvert.DeserializeObject<ExportData>(json);
            if (SolutionName != import.SolutionName)
            {
                var window = new ImportGroupsConfirmOverwriteWindow();
                if (window.ShowDialog() == true)
                {
                    TranslateSolutionPaths(import, SolutionName);
                }
            }
            SaveGroupsForSolution(import.SolutionName, import.Groups);
            if (SolutionName == import.SolutionName)
            {
                LoadGroups();
            }
        }

        private static void TranslateSolutionPaths(ExportData import, string newSolutionName)
        {
            var newSolutionPath = Path.GetDirectoryName(newSolutionName);
            var originalSolutionName = import.SolutionName;
            var originalSolutionPath = Path.GetDirectoryName(originalSolutionName);
            foreach (var group in import.Groups)
            {
                var newFiles = new DocumentFilesHashSet();
                foreach (var file in group.Files)
                {
                    if (file.StartsWith(originalSolutionPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var fileRelativePath = file.Substring(originalSolutionPath.Length + 1);
                        newFiles.Add(Path.Combine(newSolutionPath, fileRelativePath));
                    }
                    else
                    {
                        newFiles.Add(file);
                    }
                }
                group.Files = newFiles;
                group.Description = String.Join(", ", group.Files.Select(Path.GetFileName));
                group.Positions = null;
            }
            import.SolutionName = newSolutionName;
        }

        public int? FindFreeSlot()
        {
            var slotted = Groups.Where(g => g.Slot.HasValue)
                                .OrderBy(g => g.Slot)
                                .ToList();

            if (!slotted.Any())
            {
                return SlotMin;
            }

            for (var i = SlotMin; i <= SlotMax; i++)
            {
                if (slotted.All(g => g.Slot != i))
                {
                    return i;
                }
            }

            return null;
        }

        private const string SolutionGroupsFileName = "SaveAllTheTabs.json";
        private static string GetSolutionGroupsFilePath(string solutionName) => (solutionName == null) ? null :
            Path.Combine(
                Path.GetDirectoryName(solutionName),
                ".vs",
                Path.GetFileNameWithoutExtension(solutionName),
                SolutionGroupsFileName);
        private static bool SolutionGroupsFileExists(string solutionName) => File.Exists(GetSolutionGroupsFilePath(solutionName));

        public bool SolutionGroupsSavedInSettingsStore(string solutionName)
            => SolutionGroupsFileExists(solutionName);
        public void ToggleSolutionGroupsSavedInSettingsStore(string solutionName)
        {
            var filePath = GetSolutionGroupsFilePath(solutionName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            else
            {
                File.WriteAllText(filePath, "");
            }
            SaveGroupsForSolution(solutionName);
        }

        private List<DocumentGroup> LoadGroupsForSolution()
        {
            var solutionName = SolutionName;
            if (!string.IsNullOrWhiteSpace(solutionName))
            {
                try
                {
                    if (SolutionGroupsFileExists(solutionName))
                    {
                        return LoadGroupsFromSolutionFile(solutionName);
                    }
                    return LoadGroupsFromSettingsStore(solutionName);
                }
                catch (Exception ex)
                {
                    Debug.Assert(false, nameof(LoadGroupsForSolution), ex.ToString());
                }
            }
            return new List<DocumentGroup>();
        }

        private List<DocumentGroup> LoadGroupsFromSolutionFile(string solutionName)
        {
            var filePath = GetSolutionGroupsFilePath(solutionName);
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<DocumentGroup>();
            }
            var import = JsonConvert.DeserializeObject<ExportData>(json);
            if (solutionName != import.SolutionName)
                TranslateSolutionPaths(import, solutionName);
            return import.Groups;
        }

        private List<DocumentGroup> LoadGroupsFromSettingsStore(string solutionName)
        {
            var settingsMgr = new ShellSettingsManager(ServiceProvider);
            var store = settingsMgr.GetReadOnlySettingsStore(SettingsScope.UserSettings);

            var propertyName = String.Format(SavedTabsStoragePropertyFormat, solutionName);
            if (store.PropertyExists(StorageCollectionPath, propertyName))
            {
                var tabs = store.GetString(StorageCollectionPath, propertyName);
                return JsonConvert.DeserializeObject<List<DocumentGroup>>(tabs);
            }
            else if (store.PropertyExists(StorageCollectionPath, $"{propertyName}.0"))
            {
                var n = 0;
                var tabs = "";
                while (store.PropertyExists(StorageCollectionPath, $"{propertyName}.{n}"))
                {
                    tabs += store.GetString(StorageCollectionPath, $"{propertyName}.{n}");
                    n += 1;
                }
                return JsonConvert.DeserializeObject<List<DocumentGroup>>(tabs);
            }
            return new List<DocumentGroup>();
        }

        private void SaveGroupsForSolution(string solutionName = null, IList<DocumentGroup> groups = null)
        {
            if (solutionName == null)
            {
                solutionName = SolutionName;
            }
            if (string.IsNullOrWhiteSpace(solutionName))
            {
                return;
            }

            if (groups == null)
            {
                groups = Groups;
            }


            if (SolutionGroupsFileExists(solutionName))
            {
                SaveGroupsToSolutionFile(solutionName, groups);
            }
            else
            {
                SaveGroupsToSettingsStore(solutionName, groups);
            }
        }

        private void SaveGroupsToSolutionFile(string solutionName, IList<DocumentGroup> groups)
        {
            if (string.IsNullOrWhiteSpace(solutionName)) throw new ArgumentException("solutionName is not specified.", nameof(solutionName));
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            var export = new ExportData
            {
                SolutionName = SolutionName,
                Groups = Groups.ToList()
            };
            var json = JsonConvert.SerializeObject(export, Formatting.Indented);
            var filePath = GetSolutionGroupsFilePath(solutionName);
            File.WriteAllText(filePath, json);
        }

        private void SaveGroupsToSettingsStore(string solutionName, IList<DocumentGroup> groups)
        {
            if (string.IsNullOrWhiteSpace(solutionName)) throw new ArgumentException("solutionName is not specified.", nameof(solutionName));
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            var settingsMgr = new ShellSettingsManager(ServiceProvider);
            var store = settingsMgr.GetWritableSettingsStore(SettingsScope.UserSettings);

            if (!store.CollectionExists(StorageCollectionPath))
            {
                store.CreateCollection(StorageCollectionPath);
            }

            var propertyName = String.Format(SavedTabsStoragePropertyFormat, solutionName);
            if (!groups.Any())
            {
                store.DeleteProperty(StorageCollectionPath, propertyName);
                var n = 0;
                while (store.PropertyExists(StorageCollectionPath, $"{propertyName}.{n}"))
                {
                    store.DeleteProperty(StorageCollectionPath, $"{propertyName}.{n}");
                    n += 1;
                }
                return;
            }

            var tabs = JsonConvert.SerializeObject(groups);

            if (tabs.Length < StorageStringMaxLength)
            {
                store.SetString(StorageCollectionPath, propertyName, tabs);
            }
            else
            {
                if (store.PropertyExists(StorageCollectionPath, propertyName))
                {
                    store.DeleteProperty(StorageCollectionPath, propertyName);
                }
                var n = 0;
                while (store.PropertyExists(StorageCollectionPath, $"{propertyName}.{n}"))
                {
                    store.DeleteProperty(StorageCollectionPath, $"{propertyName}.{n}");
                    n += 1;
                }
                n = 0;
                while (tabs.Length > StorageStringMaxLength)
                {
                    store.SetString(StorageCollectionPath, $"{propertyName}.{n}", tabs.Substring(0, StorageStringMaxLength));
                    tabs = tabs.Substring(StorageStringMaxLength);
                    n += 1;
                }
                store.SetString(StorageCollectionPath, $"{propertyName}.{n}", tabs);
            }
        }

        public static bool IsStashGroup(string name)
        {
            return (name?.Equals(StashGroupName, StringComparison.InvariantCultureIgnoreCase)).GetValueOrDefault();
        }

        public static bool IsUndoGroup(string name)
        {
            return (name?.Equals(UndoGroupName, StringComparison.InvariantCultureIgnoreCase)).GetValueOrDefault();
        }

        public static bool IsBuiltInGroup(string name)
        {
            return IsUndoGroup(name) || IsStashGroup(name);
        }
    }
}
