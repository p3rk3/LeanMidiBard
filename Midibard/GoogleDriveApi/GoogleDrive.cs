using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Melanchall.DryWetMidi.Core;
using MidiBard.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Dalamud.api;

namespace MidiBard.GoogleDriveApi
{
    internal static class GoogleDrive
    {
        internal static readonly string ApplicationName = "LeanMidiBard";

        internal static readonly string MimeTypeFolder = "application/vnd.google-apps.folder";
        internal static readonly string MimeTypeMidi = "audio/mid";
        internal static readonly string MimeTypeJson = "application/json";
        internal static readonly string MimeTypeDefault = "application/octet-stream";

        private static string ParentFolderId;
        private static string ApiKey;
        private static ServiceAccountCredential Credential;

        internal static bool HasApiKey => MidiBard.config.GoogleDriveKey?.Length == 72;
        internal static bool HasCredential => Credential != null;

        internal static (string, string) GetIdAndApiKey(string link)
        {
            return link?.Length != 72 ? (null, null) : (link[..33], link[33..]);
        }

        internal static string GetId(string link)
        {
            return link?.Length != 72 ? null : link[..33];
        }

        internal static string GetApiKey(string link)
        {
            return link?.Length != 72 ? null : link[33..];
        }

        internal static async Task<(string name, string parentId, List<(string name, string id)> folders, List<SongEntry> songs)> Sync(string folderId, string apiKey)
        {
            ParentFolderId = folderId;
            ApiKey = apiKey;

            PluginLog.Debug($"[Sync] -> {folderId} START");
            var stopwatch = Stopwatch.StartNew();

            var items = new List<SongEntry>();
            var folders = new List<(string name, string id)>();
            string name = null;
            string parentId = null;

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { ApiKey = ApiKey, HttpClientInitializer = Credential, ApplicationName = ApplicationName });

                    var fileReq = service.Files.Get(folderId);
                    fileReq.Fields = "name, parents";
                    var fileRes = fileReq.Execute();
                    name = fileRes.Name;
                    parentId = fileRes.Parents?[0];

                    string pageToken = null;
                    do
                    {
                        var listReq = service.Files.List();
                        listReq.Q = $"'{folderId}' in parents";
                        listReq.Fields = "nextPageToken, files(id, name, mimeType)";
                        listReq.PageToken = pageToken;

                        var listRes = listReq.Execute();
                        pageToken = listRes.NextPageToken;
                        if (listRes.Files != null && listRes.Files.Count > 0)
                        {
                            foreach (var file in listRes.Files)
                            {
                                if (MimeTypeMidi.Equals(file.MimeType) || file.Name.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
                                {
                                    items.Add(new SongEntry { FilePath = file.Id, FileName = Path.GetFileNameWithoutExtension(file.Name) });
                                }
                                else if (MimeTypeFolder.Equals(file.MimeType))
                                {
                                    folders.Add((" |- " + file.Name, file.Id));
                                }
                                else if (folderId.Equals(file.Name))
                                {
                                    try
                                    {
                                        using (var streamEncoded = new MemoryStream())
                                        {
                                            var request = service.Files.Get(file.Id);
                                            request.Download(streamEncoded);
                                            streamEncoded.Seek(0, SeekOrigin.Begin);
                                            var key = ParentFolderId + ApiKey;
                                            var clearBytes = streamEncoded.ToArray().Decrypt(key);
                                            using (var streamClear = new MemoryStream(clearBytes))
                                            {
                                                Credential = ServiceAccountCredential.FromServiceAccountData(streamClear);
                                                Credential.Scopes = new[] { DriveService.Scope.Drive };
                                            }                                                
                                        }                                            
                                    }
                                    catch (Exception ex)
                                    {
                                        PluginLog.Warning(ex, $"Failed to download encrypted credential {file.Name}");
                                    }
                                }
                            }
                        }
                    } while (pageToken != null);

                    items.Sort((a, b) => a.FileName.CompareTo(b.FileName));
                    folders.Sort((a, b) => a.name.CompareTo(b.name));

                    PluginLog.Debug($"[Sync] -> {folderId} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to download files list {folderId}");
                }
            });

            return (name, parentId, folders, items);
        }

        internal static async Task<MidiFile> DownloadMidiFile(string fileId)
        {
            PluginLog.Debug($"[DownloadMidiFile] -> {fileId} START");
            var stopwatch = Stopwatch.StartNew();

            MidiFile loaded = null;
            
            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { ApiKey = ApiKey, HttpClientInitializer = Credential, ApplicationName = ApplicationName });
                    var request = service.Files.Get(fileId);
                    using (var ms = new MemoryStream())
                    {
                        request.Download(ms);
                        ms.Seek(0, SeekOrigin.Begin);
                        loaded = MidiFile.Read(ms, PlaylistManager.readingSettings);
                    }                                        

                    PluginLog.Debug($"[DownloadMidiFile] -> {fileId} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to download file {fileId}");
                }
            });

            return loaded;
        }

        internal static async Task<string> CreateFolder(string name)
        {
            PluginLog.Debug($"[CreateFolder] -> {name} START");
            var stopwatch = Stopwatch.StartNew();

            var fileMetadata = new Google.Apis.Drive.v3.Data.File() { Name = name, MimeType = MimeTypeFolder, Parents = new List<string> { ParentFolderId } };
            var id = string.Empty;

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { HttpClientInitializer = Credential, ApplicationName = ApplicationName });
                    var request = service.Files.Create(fileMetadata);
                    request.Fields = "id";
                    var file = request.Execute();
                    id = file.Id;

                    PluginLog.Debug($"[CreateFolder] -> {id} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to create folder {name}");
                }
            });

            return id;
        }

        internal static async Task<List<SongEntry>> SyncFolder(string folderId, List<SongEntry> itemsLocal)
        {
            PluginLog.Debug($"[SyncFolder] -> {folderId} START");
            var stopwatch = Stopwatch.StartNew();

            var itemsRemote = new List<SongEntry>();

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { HttpClientInitializer = Credential, ApplicationName = ApplicationName });
                    string pageToken = null;
                    do
                    {
                        var listRequest = service.Files.List();
                        listRequest.Q = $"'{folderId}' in parents";
                        listRequest.Fields = "nextPageToken, files(id, name, mimeType)";
                        listRequest.PageToken = pageToken;

                        var res = listRequest.Execute();
                        pageToken = res.NextPageToken;
                        if (res.Files != null && res.Files.Count > 0)
                        {
                            foreach (var file in res.Files)
                            {
                                if (MimeTypeMidi.Equals(file.MimeType) || file.Name.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
                                {
                                    itemsRemote.Add(new SongEntry { FilePath = file.Id, FileName = file.Name });
                                }
                            }
                        }
                    } while (pageToken != null);

                    if (itemsLocal.Count == 0)
                    {
                        service.Files.Delete(folderId).Execute();
                    }
                    else
                    {
                        var itemsToDelete = itemsRemote.ExceptBy(itemsLocal.Select(x => x.FileName), x => x.FileName).ToList();
                        foreach (var item in itemsToDelete)
                        {
                            var fileId = item.FilePath;
                            try
                            {
                                service.Files.Delete(fileId).Execute();
                            }
                            catch (Exception ex)
                            {
                                PluginLog.Warning(ex, $"Failed to delete item {fileId}");
                            }
                        }

                        var itemsToUpload = itemsLocal.ExceptBy(itemsRemote.Select(x => x.FileName), x => x.FileName).Where(x => Path.IsPathFullyQualified(x.FilePath)).ToList();
                        foreach (var item in itemsToUpload)
                        {
                            if (!File.Exists(item.FilePath))
                            {
                                PluginLog.Warning($"Failed to upload item {item.FilePath}, file does not exist");
                            }
                            else
                            {
                                var fileMetadata = new Google.Apis.Drive.v3.Data.File() { Name = item.FileName, Parents = new List<string> { folderId } };
                                using (var stream = new FileStream(item.FilePath, FileMode.Open))
                                {
                                    var request = service.Files.Create(fileMetadata, stream, MimeTypeMidi);
                                    request.Fields = "id";
                                    var response = request.Upload();

                                    if (response.Status == Google.Apis.Upload.UploadStatus.Failed)
                                    {
                                        PluginLog.Warning(response.Exception, $"Failed to upload item {item.FilePath}");
                                    }
                                    else
                                    {
                                        itemsRemote.Add(new SongEntry { FileName = item.FileName, FilePath = request.ResponseBody.Id });
                                    }
                                }
                            }
                        }
                    }

                    itemsRemote.Sort((a, b) => a.FileName.CompareTo(b.FileName));

                    PluginLog.Debug($"[SyncFolder] -> {folderId} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to sync personal folder {folderId}");
                }
            });

            return itemsRemote;
        }

        internal static async Task AddCredential(string filePath)
        {
            PluginLog.Debug($"[AddCredential] -> {filePath} START");
            var stopwatch = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                try
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(filePath)))
                    {
                        Credential = ServiceAccountCredential.FromServiceAccountData(stream);
                        Credential.Scopes = new[] { DriveService.Scope.Drive };

                        var key = ParentFolderId + ApiKey;
                        var credentialEncrypted = stream.ToArray().Encrypt(key);
                        using (var streamEncoded = new MemoryStream(credentialEncrypted))
                        {
                            var service = new DriveService(new BaseClientService.Initializer() { HttpClientInitializer = Credential, ApplicationName = ApplicationName });
                            var fileMetadata = new Google.Apis.Drive.v3.Data.File() { Name = ParentFolderId, Parents = new List<string> { ParentFolderId } };
                            var request = service.Files.Create(fileMetadata, streamEncoded, MimeTypeDefault);
                            request.Fields = "id";
                            var response = request.Upload();

                            if (response.Status == Google.Apis.Upload.UploadStatus.Failed)
                            {
                                PluginLog.Warning(response.Exception, $"Failed to upload encrypted credential {ParentFolderId}");
                            }
                        }                         
                    }

                    PluginLog.Debug($"[AddCredential] -> {filePath} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to load private key at {filePath}");
                }
            });
        }

        internal static async Task<List<(string parentId, string id)>> GetParentIdAndIdByName(string name)
        {
            PluginLog.Debug($"[GetParentIdAndIdByName] -> {name} START");
            var stopwatch = Stopwatch.StartNew();

            var items = new List<(string parentId, string id)>();

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { ApiKey = ApiKey, HttpClientInitializer = Credential, ApplicationName = ApplicationName });

                    string pageToken = null;
                    do
                    {
                        var listRequest = service.Files.List();
                        listRequest.Q = $"name = \"{name}\"";
                        listRequest.Fields = "nextPageToken, files(id, parents)";
                        listRequest.PageToken = pageToken;

                        var res = listRequest.Execute();
                        pageToken = res.NextPageToken;
                        if (res.Files != null && res.Files.Count > 0)
                        {
                            foreach (var file in res.Files)
                            {
                                items.Add((file.Parents?[0], file.Id));
                            }
                        }
                    } while (pageToken != null);

                    PluginLog.Debug($"[GetParentIdAndIdByName] -> {name} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to get files with name {name}");
                }
            });

            return items;
        }

        internal static async Task<string> CreateText(string folderId, string fileName, string content)
        {
            PluginLog.Debug($"[CreateText] -> {fileName} START");
            var stopwatch = Stopwatch.StartNew();

            string id = string.Empty;

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { HttpClientInitializer = Credential, ApplicationName = ApplicationName });
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File() { Name = fileName, Parents = new List<string> { folderId } };

                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                    {                        
                        var request = service.Files.Create(fileMetadata, stream, MimeTypeJson);
                        request.Fields = "id";
                        var response = request.Upload();

                        id = request.ResponseBody.Id;

                        if (response.Status == Google.Apis.Upload.UploadStatus.Failed)
                        {
                            PluginLog.Warning(response.Exception, $"Failed to upload text {folderId}");
                        }
                        else
                        {
                            PluginLog.Debug($"[CreateText] -> {fileName} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to create text {fileName}");
                }
            });

            return id;
        }

        internal static async Task UpdateText(string fileId, string content)
        {
            PluginLog.Debug($"[UpdateText] -> {fileId} START");
            var stopwatch = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { HttpClientInitializer = Credential, ApplicationName = ApplicationName });
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File();

                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                    {
                        var request = service.Files.Update(fileMetadata, fileId, stream, MimeTypeJson);
                        var response = request.Upload();

                        if (response.Status == Google.Apis.Upload.UploadStatus.Failed)
                        {
                            PluginLog.Warning(response.Exception, $"Failed to update text {fileId}");
                        }
                        else
                        {
                            PluginLog.Debug($"[UpdateText] -> {fileId} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to update text {fileId}");
                }
            });
        }

        internal static async Task<string> ReadText(string fileId)
        {
            PluginLog.Debug($"[ReadJson] -> {fileId} START");
            var stopwatch = Stopwatch.StartNew();

            var json = string.Empty;

            await Task.Run(() =>
            {
                try
                {
                    var service = new DriveService(new BaseClientService.Initializer() { ApiKey = ApiKey, HttpClientInitializer = Credential, ApplicationName = ApplicationName });

                    var request = service.Files.Get(fileId);
                    using (var stream = new MemoryStream())
                    {
                        request.Download(stream);

                        json = Encoding.UTF8.GetString(stream.ToArray());
                    }

                    PluginLog.Debug($"[ReadJson] -> {fileId} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, $"Failed to read JSON {fileId}");
                }
            });

            return json;
        }
    }
}