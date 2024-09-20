using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ImGuiNET;
using MidiBard.GoogleDriveApi;
using MidiBard.IPC;
using MidiBard.Managers;
using MidiBard.Managers.Ipc;
using Newtonsoft.Json;
using static ImGuiNET.ImGui;
using static MidiBard.ImGuiUtil;
using static Dalamud.api;

namespace MidiBard;

public partial class PluginUI
{
    private static int UIcurrentPlaylistFolder;

    private unsafe void DrawCloudBar(int foldersWidth = 244)
    {
        ButtonSyncWithGoogleDrive();
        SameLine();
        if (PlaylistManager.FolderList.Count > 0)
        {
            SetNextItemWidth(ImGuiHelpers.GlobalScale * foldersWidth);
            ComboBoxGoogleDriveFolders();

            if (api.ClientState.LocalPlayer != null)
            {
                SameLine();
                if (IconButton(FontAwesomeIcon.Download, "DownloadConfig", "Download ensemble config and equip the assigned instrument"))
                {
                    DownloadConfigAndEquipInstrument();
                }
            }
        }
        else
        {
            TextBoxGoogleDriveLink();
        }        

        Separator();
    }

    private void TextBoxGoogleDriveLink()
    {
        SetNextItemWidth(-1);
        InputTextWithHint("##googledrivelink", "Link", ref MidiBard.config.GoogleDriveKey, 72, ImGuiInputTextFlags.AutoSelectAll);
        if (IsItemHovered() && IsMouseClicked(ImGuiMouseButton.Right))
        {
            RunImportPrivateKeyTask();
        }
    }

    private void ButtonSyncWithGoogleDrive()
    {
        if (IconButton(FontAwesomeIcon.Sync, "syncbutton") && GoogleDrive.HasApiKey)
        {
            if (api.ClientState.LocalPlayer != null && GoogleDrive.HasCredential)
            {
                var myName = $"{api.ClientState.LocalPlayer.Name}·{api.ClientState.LocalPlayer.HomeWorld.GameData.Name}";
                var myFolderIndex = PlaylistManager.FolderList.FindIndex(x => x.name == myName);
                if (UIcurrentPlaylistFolder == myFolderIndex)
                {
                    var folderId = PlaylistManager.FolderList[myFolderIndex].id;
                    SyncMyPlaylistFolder(folderId);

                    return;
                }
            }

            if (PlaylistManager.FolderList.Count > 0)
            {
                var folderId = PlaylistManager.FolderList[UIcurrentPlaylistFolder].id;
                if (folderId != null)
                {
                    SyncSelectedPlaylistFolder(folderId);
                }
            }
            else
            {
                SyncRootPlaylistFolder();
            }
        }

        ToolTip("Sync with cloud\r\nSync your personal folder");
    }

    private void ComboBoxGoogleDriveFolders()
    {
        if (BeginCombo(string.Empty, PlaylistManager.FolderList[UIcurrentPlaylistFolder].name, ImGuiComboFlags.HeightLarge))
        {
            GetWindowDrawList().ChannelsSplit(2);
            for (int i = 0; i < PlaylistManager.FolderList.Count; i++)
            {
                var folder = PlaylistManager.FolderList[i];
                GetWindowDrawList().ChannelsSetCurrent(1);
                AlignTextToFramePadding();
                if (Selectable($"{folder.name}##{i}", UIcurrentPlaylistFolder == i, ImGuiSelectableFlags.SpanAllColumns))
                {
                    if (folder.id == null)
                    {
                        PlaylistManager.Clear();
                        PlaylistManager.FilePathList.AddRange(PlaylistManager.FilePathListLocal);

                        UIcurrentPlaylistFolder = i;
                    }
                    else
                    {
                        SyncSelectedPlaylistFolder(folder.id);
                    }
                }
            }
            GetWindowDrawList().ChannelsMerge();
            EndCombo();
        }

        ToolTip("Folders\r\nRight click to enter new link");

        if (IsItemHovered() && IsMouseClicked(ImGuiMouseButton.Right))
        {
            PlaylistManager.FolderList.Clear();
        }
    }

    private Task SyncRootPlaylistFolder()
    {
        return Task.Run(async () =>
        {
            try
            {
                IsImportRunning = true;

                var (parentFolderId, apiKey) = GoogleDrive.GetIdAndApiKey(MidiBard.config.GoogleDriveKey);
                var (name, parentId, folders, items) = await GoogleDrive.Sync(parentFolderId, apiKey);
                PlaylistManager.FoldersRemoteRoot.Clear();
                PlaylistManager.FoldersRemoteRoot.AddRange(folders);

                PlaylistManager.FilePathListLocal.AddRange(PlaylistManager.FilePathList);

                PlaylistManager.Clear();
                PlaylistManager.FilePathList.AddRange(items);

                PlaylistManager.FolderList.Clear();
                PlaylistManager.FolderList.Add((name, parentFolderId));
                PlaylistManager.FolderList.AddRange(folders);

                if (api.ClientState.LocalPlayer != null && GoogleDrive.HasCredential)
                {
                    var myName = $"{api.ClientState.LocalPlayer.Name}·{api.ClientState.LocalPlayer.HomeWorld.GameData.Name}";
                    var myFolderId = folders.FirstOrDefault(x => x.name.EndsWith(myName)).id;
                    if (myFolderId == null)
                    {
                        myFolderId = await GoogleDrive.CreateFolder(myName);
                        PlaylistManager.FolderList.Add((myName, myFolderId));
                    }
                }

                PlaylistManager.FolderList.Add((" < Local Playlist > ", null));

                UIcurrentPlaylistFolder = 0;
            }
            finally
            {
                IsImportRunning = false;
            }
        });
    }

    private Task SyncMyPlaylistFolder(string folderId)
    {
        return Task.Run(async () =>
        {
            try
            {
                IsImportRunning = true;

                var myItems = await GoogleDrive.SyncFolder(folderId, PlaylistManager.FilePathList);
                PlaylistManager.Clear();
                PlaylistManager.FilePathList.AddRange(myItems);
            }
            finally
            {
                IsImportRunning = false;
            }
        });
    }

    private Task SyncSelectedPlaylistFolder(string folderId)
    {
        return Task.Run(async () =>
        {
            try
            {
                IsImportRunning = true;

                var apiKey = GoogleDrive.GetApiKey(MidiBard.config.GoogleDriveKey);
                var (name, parentId, folders, items) = await GoogleDrive.Sync(folderId, apiKey);

                if (PlaylistManager.FolderList[UIcurrentPlaylistFolder].id == null)
                {
                    PlaylistManager.FilePathListLocal.Clear();
                    PlaylistManager.FilePathListLocal.AddRange(PlaylistManager.FilePathList);
                }

                PlaylistManager.Clear();
                PlaylistManager.FilePathList.AddRange(items);

                PlaylistManager.FolderList.Clear();
                PlaylistManager.FolderList.Add((name, folderId));
                PlaylistManager.FolderList.AddRange(folders);
                PlaylistManager.FolderList.Add((" < Local Playlist > ", null));
                if (parentId != null)
                {
                    PlaylistManager.FolderList.Insert(0, ("..", parentId));

                    UIcurrentPlaylistFolder = 1;
                }
                else
                {
                    UIcurrentPlaylistFolder = 0;
                }
            }
            finally
            {
                IsImportRunning = false;
            }
        });
    }

    private void DownloadConfigAndEquipInstrument()
    {
        Task.Run(async () =>
        {
            try
            {
                IsImportRunning = true;

                var name = api.PartyList.GetPartyLeader()?.NameAndWorld() ?? $"{api.ClientState.LocalPlayer.Name}·{api.ClientState.LocalPlayer.HomeWorld.GameData.Name}";
                var folderId = PlaylistManager.FoldersRemoteRoot.FirstOrDefault(x => x.name.EndsWith(name)).id;
                if (folderId == null)
                {
                    PluginLog.Warning($"Folder not found for party leader {name}");
                    return;
                }

                var configId = PlaylistManager.ConfigsRemoteRoot.FirstOrDefault(x => x.parentId == folderId).id;
                if (configId == null)
                {
                    var configs = await GoogleDrive.GetParentIdAndIdByName(ConfigName);
                    PlaylistManager.ConfigsRemoteRoot.Clear();
                    PlaylistManager.ConfigsRemoteRoot.AddRange(configs);

                    configId = PlaylistManager.ConfigsRemoteRoot.FirstOrDefault(x => x.parentId == folderId).id;
                    if (configId == null)
                    {
                        PluginLog.Warning($"Config not found for party leader {name}");
                        return;
                    }
                }

                var json = await GoogleDrive.ReadText(configId);
                var config = JsonConvert.DeserializeObject<MidiFileConfig>(json);

                if (PlaylistManager.FolderList[UIcurrentPlaylistFolder].id != config.FolderId || PlaylistManager.FilePathList.FindIndex(x => x.FilePath == config.FilePath) == -1)
                {
                    await SyncSelectedPlaylistFolder(config.FolderId);
                }

                if (MidiBard.CurrentPlayback == null || PlaylistManager.CurrentSongIndex < 0 || PlaylistManager.FilePathList[PlaylistManager.CurrentSongIndex].FilePath != config.FilePath)
                {
                    var songIndex = PlaylistManager.FilePathList.FindIndex(x => x.FilePath == config.FilePath);
                    if (songIndex == -1)
                    {
                        PluginLog.Warning($"Song {config.FilePath} not found in the folder {config.FolderId}");
                        return;
                    }

                    if (!await PlaylistManager.LoadPlayback(songIndex, false, false))
                    {
                        PluginLog.Warning("Load playback failed");
                    }
                }

                MidiBard.config.TransposeGlobal = config.Transpose;
                MidiBard.config.GuitarToneMode = config.ToneMode;
                MidiBard.config.AdaptNotesOOR = config.AdaptNotes;
                MidiBard.config.PlaySpeed = config.Speed;

                IPCHandles.UpdateMidiFileConfig(config);
                IPCHandles.UpdateInstrument(true);
            }
            finally
            {
                IsImportRunning = false;
            }
        });
    }
}