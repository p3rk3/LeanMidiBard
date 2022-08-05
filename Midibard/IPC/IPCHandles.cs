﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Logging;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl;
using MidiBard.DalamudApi;
using MidiBard.Managers;
using MidiBard.Managers.Agents;
using MidiBard.Managers.Ipc;
using MidiBard.Util;
using MidiBard.Control.MidiControl.PlaybackInstance;
using Melanchall.DryWetMidi.Interaction;

namespace MidiBard.IPC;

public enum MessageTypeCode
{
	Hello = 1,
	Bye,
	Acknowledge,

	GetMaster,
	SetSlave,
	SetUnslave,

	SyncPlaylist = 10,
	RemoveTrackIndex,
	LoadPlaybackIndex,

	UpdateMidiFileConfig = 20,
	UpdateEnsembleMember,
	MidiEvent,
	SetInstrument,
	EnsembleStartTime,
	UpdateDefaultPerformer,

	SetOption = 100,
	ShowWindow,
	SyncAllSettings,
	Object,
	SyncPlayStatus,
	PlaybackSpeed,
	GlobalTranspose,
	MoveToTime,
	PlayOnMultipleDevices,

	ErrPlaybackNull = 1000
}

static class IPCHandles
{
	public static void SyncPlaylist()
	{
		if (!MidiBard.config.SyncClients) return;
		IPCEnvelope.Create(MessageTypeCode.SyncPlaylist, MidiBard.config.Playlist.ToArray()).BroadCast();
	}

	[IPCHandle(MessageTypeCode.SyncPlaylist)]
	private static void HandleSyncPlaylist(IPCEnvelope message)
	{
		var paths = message.StringData;
		Task.Run(() => PlaylistManager.AddAsync(paths, true, true));
	}

	public static void RemoveTrackIndex(int index)
	{
		if (!MidiBard.config.SyncClients) return;
		IPCEnvelope.Create(MessageTypeCode.RemoveTrackIndex, index).BroadCast();
	}

	[IPCHandle(MessageTypeCode.RemoveTrackIndex)]
	private static void HandleRemoveTrackIndex(IPCEnvelope message)
	{
		PlaylistManager.RemoveLocal(message.DataStruct<int>());
	}

	public static void UpdateMidiFileConfig(MidiFileConfig config, bool updateInstrumentAfterFinished = false)
	{
		if (!MidiBard.config.SyncClients) return;
		if (!api.PartyList.IsPartyLeader() || api.PartyList.Length < 2) return;
        string[] strings = new string[2] { config.JsonSerialize(), updateInstrumentAfterFinished.ToString()};
		IPCEnvelope.Create(MessageTypeCode.UpdateMidiFileConfig, strings).BroadCast(true);
	}

	[IPCHandle(MessageTypeCode.UpdateMidiFileConfig)]
	private static void HandleUpdateMidiFileConfig(IPCEnvelope message)
	{
		var midiFileConfig = message.StringData[0].JsonDeserialize<MidiFileConfig>();
		bool updateInstrumentAfterFinished = message.StringData[1].ToString() == "True";
		while (MidiBard.CurrentPlayback == null)
        {
			System.Threading.Thread.Sleep(5);
        }
		MidiBard.CurrentPlayback.MidiFileConfig = midiFileConfig;
		var dbTracks = midiFileConfig.Tracks;
		var trackStatus = MidiBard.config.TrackStatus;
		for (var i = 0; i < dbTracks.Count; i++)
		{
			try
			{
				trackStatus[i].Enabled = dbTracks[i].Enabled && MidiFileConfig.GetFirstCidInParty(dbTracks[i]) == (long)api.ClientState.LocalContentId;
				trackStatus[i].Transpose = dbTracks[i].Transpose;
				trackStatus[i].Tone = InstrumentHelper.GetGuitarTone(dbTracks[i].Instrument);
			}
			catch (Exception e)
			{
				PluginLog.Error(e, $"error when updating track {i}");
			}
		}

		MidiBard.config.EnableTransposePerTrack = true;
		
		if (updateInstrumentAfterFinished)
        {
			UpdateInstrument(true);
        }
	}

	public static void LoadPlayback(int index, bool includeSelf = false)
	{
		if (!MidiBard.config.SyncClients) return;
		if (!api.PartyList.IsPartyLeader() || api.PartyList.Length < 2) return;
		IPCEnvelope.Create(MessageTypeCode.LoadPlaybackIndex, index).BroadCast(includeSelf);
	}
	[IPCHandle(MessageTypeCode.LoadPlaybackIndex)]
	private static void HandleLoadPlayback(IPCEnvelope message)
	{
		FilePlayback.LoadPlayback(message.DataStruct<int>(), false, false);
	}

	public static void UpdateInstrument(bool takeout)
	{
		if (!MidiBard.config.SyncClients) return;
		if (!api.PartyList.IsPartyLeader() || api.PartyList.Length < 2) return;
		IPCEnvelope.Create(MessageTypeCode.SetInstrument, takeout).BroadCast(true);
	}
	[IPCHandle(MessageTypeCode.SetInstrument)]
	private static void HandleSetInstrument(IPCEnvelope message)
	{
		var takeout = message.DataStruct<bool>();
		if (!takeout)
		{
			SwitchInstrument.SwitchToContinue(0);
			MidiPlayerControl.Stop();
			return;
		}

		while (MidiBard.CurrentPlayback == null)
		{
			System.Threading.Thread.Sleep(500);
		}

		uint? instrument = null;
		foreach(var cur in MidiBard.CurrentPlayback.MidiFileConfig.Tracks)
        {
			if (cur.Enabled && MidiFileConfig.IsCidOnTrack((long)api.ClientState.LocalContentId, cur))
			{ 
				instrument = (uint?)cur.Instrument;
				break;
			}
        }

		if (instrument != null)
			SwitchInstrument.SwitchToContinue((uint)instrument);
		}

	//public static void DoMacro(string[] lines, bool includeSelf = false)
	//{
	//	IPCEnvelope.Create(MessageTypeCode.Macro, lines).BroadCast(includeSelf);
	//}
	//[IPCHandle(MessageTypeCode.Macro)]
	//private static void HandleDoMacro(IPCEnvelope message)
	//{
	//	ChatCommands.DoMacro(message.StringData);
	//}

	public static void SetOption(ConfigOption option, int value, bool includeSelf)
	{
		IPCEnvelope.Create(MessageTypeCode.SetOption, (option, value)).BroadCast(includeSelf);
	}
	[IPCHandle(MessageTypeCode.SetOption)]
	private static void HandleSetOption(IPCEnvelope message)
	{
		var dataStruct = message.DataStruct<(ConfigOption, int)>();
		AgentConfigSystem.SetOptionValue(dataStruct.Item1, dataStruct.Item2);
	}
	public static void ShowWindow(Winapi.nCmdShow option)
	{
		IPCEnvelope.Create(MessageTypeCode.ShowWindow, option).BroadCast();
	}

	[IPCHandle(MessageTypeCode.ShowWindow)]
	private static void HandleShowWindow(IPCEnvelope message)
	{
		var nCmdShow = message.DataStruct<Winapi.nCmdShow>();
		var hWnd = api.PluginInterface.UiBuilder.WindowHandlePtr;
		var isIconic = Winapi.IsIconic(hWnd);

		if (nCmdShow == Winapi.nCmdShow.SW_RESTORE)
		{
			MidiBard.Ui.Open();

			if (!isIconic)
			{
				return;
			}
		}

		if (nCmdShow == Winapi.nCmdShow.SW_MINIMIZE)
		{
			MidiBard.Ui.Close();

			if (isIconic)
			{
				return;
			}
		}

		Winapi.ShowWindow(hWnd, nCmdShow);
	}

	public static void SyncAllSettings()
	{
		IPCEnvelope.Create(MessageTypeCode.SyncAllSettings, MidiBard.config.JsonSerialize()).BroadCast();
	}

	[IPCHandle(MessageTypeCode.SyncAllSettings)]
	public static void HandleSyncAllSettings(IPCEnvelope message)
	{
		var str = message.StringData[0];
		var jsonDeserialize = str.JsonDeserialize<Configuration>();
		//do not overwrite track settings
		jsonDeserialize.TrackStatus = MidiBard.config.TrackStatus;
		MidiBard.config = jsonDeserialize;
	}

	public static void UpdateDefaultPerformer()
	{
		IPCEnvelope.Create(MessageTypeCode.UpdateDefaultPerformer, MidiFileConfigManager.defaultPerformer.JsonSerialize()).BroadCast();
	}

	[IPCHandle(MessageTypeCode.UpdateDefaultPerformer)]
	public static void HandleUpdateDefaultPerformer(IPCEnvelope message)
	{
		var str = message.StringData[0];
		var jsonDeserialize = str.JsonDeserialize<DefaultPerformer>();
		MidiFileConfigManager.defaultPerformer = jsonDeserialize;
		if (MidiBard.CurrentPlayback != null)
		{
			MidiBard.CurrentPlayback.MidiFileConfig = BardPlayback.LoadDefaultPerformer(MidiBard.CurrentPlayback.MidiFileConfig);
		}
	}

	public static void PlayOnMultipleDevices(bool playOnMultipleDevices)
	{
		IPCEnvelope.Create(MessageTypeCode.PlayOnMultipleDevices, playOnMultipleDevices).BroadCast();
	}

	[IPCHandle(MessageTypeCode.PlayOnMultipleDevices)]
	public static void HandlePlayOnMultipleDevices(IPCEnvelope message)
	{
		var playOnMultipleDevices = message.DataStruct<bool>();
		MidiBard.config.playOnMultipleDevices = playOnMultipleDevices;
	}

	public static void PlaybackSpeed(float playbackSpeed)
	{
		if (api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader()) return;
		IPCEnvelope.Create(MessageTypeCode.PlaybackSpeed, playbackSpeed).BroadCast();
	}

	[IPCHandle(MessageTypeCode.PlaybackSpeed)]
	public static void HandlePlaybackSpeed(IPCEnvelope message)
	{
		var playbackSpeed = message.DataStruct<float>();
		MidiBard.config.playSpeed = playbackSpeed;
		if (MidiBard.CurrentPlayback != null)
		{
			MidiBard.CurrentPlayback.Speed = MidiBard.config.playSpeed;
		}
	}
	
	public static void GlobalTranspose(int transpose)
	{
		if (api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader()) return;
		IPCEnvelope.Create(MessageTypeCode.GlobalTranspose, transpose).BroadCast();
	}

	[IPCHandle(MessageTypeCode.GlobalTranspose)]
	public static void HandleGlobalTranspose(IPCEnvelope message)
	{
		var globalTranspose = message.DataStruct<int>();
		MidiBard.config.SetTransposeGlobal(globalTranspose);
	}
	
	public static void MoveToTime(float progress)
	{
		if (api.PartyList.Length < 2 || !api.PartyList.IsPartyLeader()) return;
		IPCEnvelope.Create(MessageTypeCode.MoveToTime, progress).BroadCast(true);
	}

	[IPCHandle(MessageTypeCode.MoveToTime)]
	public static void HandleMoveToTime(IPCEnvelope message)
	{
		if (MidiBard.CurrentPlayback == null)
        {
			return;
        }

		var progress = message.DataStruct<float>();
		if (MidiBard.CurrentPlayback.IsRunning)
		{
			var compensation = MidiBard.CurrentInstrument switch
			{
				0 or 3 => 105,
				1 => 85,
				2 or 4 => 90,
				>= 5 and <= 8 => 95,
				9 or 10 => 90,
				11 or 12 => 80,
				13 => 85,
				>= 14 => 30
			};
			var timeSpan = MidiBard.CurrentPlayback.GetDuration<MetricTimeSpan>().Multiply(progress);
            if (MidiBard.AgentMetronome.EnsembleModeRunning)
            {
				timeSpan.Add(new MetricTimeSpan((105 - compensation) * 1000), TimeSpanMode.LengthLength);
			}
            MidiBard.CurrentPlayback.MoveToTime(timeSpan);
		} else
        {
			var timeSpan = MidiBard.CurrentPlayback.GetDuration<MetricTimeSpan>().Multiply(progress);
			MidiBard.CurrentPlayback.Stop();
			MidiBard.CurrentPlayback.MoveToTime(timeSpan);
			MidiBard.CurrentPlayback.PlaybackStart = timeSpan;
        }
	}
	
	public static void ErrPlaybackNull(string characterName)
	{
		IPCEnvelope.Create(MessageTypeCode.ErrPlaybackNull, characterName).BroadCast(true);
	}

	[IPCHandle(MessageTypeCode.ErrPlaybackNull)]
	public static void HandleErrPlaybackNull(IPCEnvelope message)
	{
		var characterName = message.StringData[0];
		PluginLog.LogWarning($"ERR: Playback Null on character: {characterName}");
		api.ChatGui.PrintError($"[MidiBard 2] Error: Load song failed on character: {characterName}, please try to switch the song again.");
	}
}