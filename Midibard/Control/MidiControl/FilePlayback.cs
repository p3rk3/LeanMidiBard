// Copyright (C) 2022 akira0245
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see https://github.com/akira0245/MidiBard/blob/master/LICENSE.
// 
// This code is written by akira0245 and was originally used in the MidiBard project. Any usage of this code must prominently credit the author, akira0245, and indicate that it was originally used in the MidiBard project.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Logging;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Standards;
using Melanchall.DryWetMidi.Tools;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl.PlaybackInstance;
using Dalamud;
using MidiBard.GoogleDriveApi;
using MidiBard.IPC;
using MidiBard.Managers.Ipc;
using MidiBard.Util;
using MidiBard.Util.Lyrics;
using static MidiBard.MidiBard;
using static Dalamud.api;

namespace MidiBard.Control.MidiControl;

public static class FilePlayback
{
	private static BardPlayback GetPlaybackInstance(MidiFile midifile, SongEntry songEntry)
	{
		PluginLog.Debug($"[LoadPlayback] -> {songEntry.FilePath} START");
		var stopwatch = Stopwatch.StartNew();
		var playback = BardPlayback.GetBardPlayback(midifile, songEntry);
		playback.InterruptNotesOnStop = true;
		playback.TrackNotes = true;
		playback.TrackProgram = true;
		playback.Speed = config.PlaySpeed;
		playback.Finished += Playback_Finished;
		PluginLog.Debug($"[LoadPlayback] -> {songEntry.FilePath} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");
		api.ChatGui.Print(String.Format("[MidiBard] Now Playing: {0}", playback.DisplayName));
		MidiBard.PluginIpc.MidiBardPlayingFileNamePub.SendMessage(playback.DisplayName);
		return playback;
	}

	internal static float waitProgress = 0;
	internal static Status waitStatus = Status.notWaiting;
	private static void Playback_Finished(object sender, EventArgs e)
	{
		Task.Run(() =>
		{
			try
			{
				if (MidiBard.AgentMetronome.EnsembleModeRunning)
					return;
				if (!PlaylistManager.FilePathList.Any())
					return;
				if (MidiBard.SlaveMode)
					return;

				var fromSeconds = TimeSpan.FromSeconds(config.SecondsBetweenTracks);
				PerformWaiting(fromSeconds, ref waitProgress, ref waitStatus);
				if (waitStatus == Status.canceled) return;

				switch ((PlayMode)config.PlayMode)
				{
					case PlayMode.Single:
						break;
					case PlayMode.SingleRepeat:
						CurrentPlayback?.MoveToTime(new MidiTimeSpan(0));
						MidiPlayerControl.DoPlay();
						break;
					case PlayMode.ListOrdered when PlaylistManager.CurrentSongIndex >= PlaylistManager.FilePathList.Count - 1:
						break;
					case PlayMode.ListOrdered:
					case PlayMode.ListRepeat:
					case PlayMode.Random:
						MidiPlayerControl.Next(true);
						break;
				}
			}
			catch (Exception exception)
			{
				PluginLog.Error(exception, "Unexpected exception when Playback finished.");
			}
		});
	}

	internal static async Task<bool> LoadPlayback(SongEntry songEntry)
	{
		MidiFile midiFile;
		var filePath = songEntry.FilePath;

		if (Path.IsPathFullyQualified(filePath))
			midiFile = await Task.Run(() => PlaylistManager.LoadSongFile(filePath));
		else
			midiFile = await GoogleDrive.DownloadMidiFile(filePath);

		if (midiFile == null)
		{
			// delete file if can't be loaded(likely to be deleted locally)
			//PluginLog.Debug($"[LoadPlayback] removing {index}");
			//PluginLog.Debug($"[LoadPlayback] removing {PlaylistManager.FilePathList[index].path}");
			//PlaylistManager.RemoveSync(index);
			return false;
		}
		else
		{
			var playback = await Task.Run(() => GetPlaybackInstance(midiFile, songEntry));
			CurrentPlayback?.Dispose();
			CurrentPlayback = playback;

			Ui.RefreshPlotData();
            MidiBard.BardPlayDevice.ResetChannelStates();

            try
            {
				await SwitchInstrument.WaitSwitchInstrumentForSong(songEntry.FileName);
			}
			catch (Exception e)
			{
				PluginLog.Warning(e.ToString());
			} finally
            {
				Lrc.InitLrc(filePath);

#if DEBUG
            //PluginLog.LogVerbose($"Title: {lrc.Title}, Artist: {lrc.Artist}, Album: {lrc.Album}, LrcBy: {lrc.LrcBy}, Offset: {lrc.Offset}");
            //foreach(var pair in lrc.LrcWord)
            //{
            //    PluginLog.LogVerbose($"{pair.Key}, {pair.Value}");
            //}
#endif
			}

			return true;
		}
	}
	public static bool IsWaiting => waitStatus == Status.waiting;
	public static float GetWaitWaitProgress => waitProgress;
	internal static void CancelWaiting() => waitStatus = Status.canceled;
	internal static void SkipWaiting() => waitStatus = Status.skipped;

	internal enum Status
	{
		notWaiting,
		waiting,
		skipped,
		canceled,
	}
	internal static void PerformWaiting(TimeSpan waitTime, ref float progress, ref Status status)
	{
		status = Status.waiting;
		progress = 0;
		var end = DateTime.UtcNow + waitTime;
		try
		{
			while (status == Status.waiting && progress < 1)
			{
				var remain = end - DateTime.UtcNow;
				progress = (float)(1 - remain / waitTime);
				Thread.Sleep(25);
				if (status != Status.waiting) return;
			}
		}
		finally
		{
			progress = 0;
		}

		status = Status.notWaiting;
	}
}