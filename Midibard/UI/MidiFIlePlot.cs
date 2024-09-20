﻿// Copyright (C) 2022 akira0245
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
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Logging;
using ImGuiNET;
using ImPlotNET;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiBard.Control;
using MidiBard.Control.MidiControl.PlaybackInstance;
using MidiBard.Managers;
using MidiBard.Managers.Agents;
using MidiBard.Resources;
using MidiBard.Util;
using static Dalamud.api;

namespace MidiBard;

public partial class PluginUI
{
    static Vector4 HSVToRGB(float h, float s, float v, float a = 1)
    {
        Vector4 c;
        ImGui.ColorConvertHSVtoRGB(h, s, v, out c.X, out c.Y, out c.Z);
        c.W = a;
        return c;
    }


    //private uint[] ChannelColorPalette = Enumerable.Range(0, 16).Select(i => ImGui.ColorConvertFloat4ToU32(HSVToRGB(i / 16f, 0.75f, 1))).ToArray();

    private bool setNextLimit;
    private double timeWindow = 10;
    private void DrawPlotWindow()
    {
        var framebg = ImGui.GetColorU32(ImGuiCol.FrameBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, framebg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, framebg);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, -Vector2.One);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.SetNextWindowSize(ImGuiHelpers.ScaledVector2(640, 480), ImGuiCond.FirstUseEver);
        if (_resetPlotWindowPosition && MidiBard.config.PlotTracks)
        {
            ImGui.SetNextWindowPos(new Vector2(100), ImGuiCond.Always);
            ImGui.SetNextWindowSize(ImGuiHelpers.ScaledVector2(640, 480), ImGuiCond.Always);
            _resetPlotWindowPosition = false;
        }
        if (ImGui.Begin(Language.window_title_visualizor + "###midibardMidiPlot", ref MidiBard.config.PlotTracks, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.PopStyleVar();
            var icon = MidiBard.config.LockPlot ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
            if (ImGuiUtil.AddHeaderIcon("lockPlot", icon.ToIconString(), Language.icon_button_tooltip_visualizer_follow_playback_tooltip))
            {
                MidiBard.config.LockPlot ^= true;
            }
            MidiPlotWindow();
        }
        else
        {
            ImGui.PopStyleVar();
        }

        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    private unsafe void MidiPlotWindow()
    {
        if (ImGui.IsWindowAppearing())
        {
            RefreshPlotData();
        }

        double timelinePos = 0;
        double? ensembleTimelinePos = null;

        try
        {
            var currentPlayback = MidiBard.CurrentPlayback;
            if (currentPlayback != null)
            {
                timelinePos = currentPlayback.GetCurrentTime<MetricTimeSpan>().GetTotalSeconds();
                if (MidiBard.config.UseEnsembleIndicator && EnsembleManager.EnsembleRunning)
                    ensembleTimelinePos = timelinePos + MidiBard.config.EnsembleIndicatorDelay - EnsembleManager.GetCompensationNew(MidiBard.CurrentInstrumentWithTone, -1) * 0.001d;
            }
        }
        catch (Exception e)
        {
            //
        }

        string songName = "";
        try
        {
            songName = PlaylistManager.FilePathList[PlaylistManager.CurrentSongIndex].FileName;
        }
        catch (Exception e)
        {
            //
        }

        //ImGui.SetCursorPos(ImGui.GetWindowContentRegionMin());
        if (ImPlot.BeginPlot(songName + "###midiTrackPlot", ImGuiUtil.GetWindowContentRegion(), ImPlotFlags.NoTitle))
        {
            ImPlot.SetupAxisLimits(ImAxis.X1, 0, 20, ImPlotCond.Once);
            ImPlot.SetupAxisLimits(ImAxis.Y1, 42, 91, ImPlotCond.Once);
            ImPlot.SetupAxisTicks(ImAxis.Y1, 0, 127, 128, noteNames, false);

            if (setNextLimit)
            {
                try
                {
                    if (!MidiBard.config.LockPlot)
                        ImPlot.SetupAxisLimits(ImAxis.X1, 0, _plotData.Select(i => i.trackInfo.DurationMetric.GetTotalSeconds()).Max(), ImPlotCond.Always);
                    setNextLimit = false;
                }
                catch (Exception e)
                {
                    PluginLog.Error(e, "error when try set next plot limit");
                }
            }

            if (MidiBard.config.LockPlot)
            {
                var imPlotRange = ImPlot.GetPlotLimits(ImAxis.X1).X;
                var d = (imPlotRange.Max - imPlotRange.Min) / 2;
                if (ensembleTimelinePos is not null)
                {
                    ImPlot.SetupAxisLimits(ImAxis.X1, (double)ensembleTimelinePos - d, (double)ensembleTimelinePos + d, ImPlotCond.Always);
                }
                else
                {
                    ImPlot.SetupAxisLimits(ImAxis.X1, timelinePos - d, timelinePos + d, ImPlotCond.Always);
                }
            }


            var drawList = ImPlot.GetPlotDrawList();
            var xMin = ImPlot.GetPlotLimits().X.Min;
            var xMax = ImPlot.GetPlotLimits().X.Max;

            //if (!MidiBard.config.LockPlot) timeWindow = (xMax - xMin) / 2;

            ImPlot.PushPlotClipRect();


            var cp = ImGuiColors.ParsedBlue;
            cp.W = 0.05f;
            drawList.AddRectFilled(ImPlot.PlotToPixels(xMin, 48 + 37), ImPlot.PlotToPixels(xMax, 48), ImGui.ColorConvertFloat4ToU32(cp));

            if (_plotData?.Any() == true && MidiBard.CurrentPlayback != null)
            {
                var legendInfoList = new List<(string trackName, Vector4 color, int index)>();

                foreach (var (trackInfo, notes) in _plotData.OrderBy(i => i.trackInfo.IsPlaying))
                {
                    Vector4 GetNoteColor()
                    {
                        var c = System.Numerics.Vector4.One;
                        try
                        {
                            ImGui.ColorConvertHSVtoRGB(trackInfo.Index / (float)MidiBard.CurrentPlayback.TrackInfos.Length, 0.8f, 1, out c.X, out c.Y, out c.Z);
                            if (!trackInfo.IsPlaying) c.W = 0.2f;
                        }
                        catch (Exception e)
                        {
                            PluginLog.Error(e, "error when getting track color");
                        }
                        return c;
                    }

                    var noteColor = GetNoteColor();
                    var noteColorRgb = ImGui.ColorConvertFloat4ToU32(noteColor);

                    legendInfoList.Add(($"[{trackInfo.Index + 1:00}] {trackInfo.TrackName}", noteColor, trackInfo.Index));


                    foreach (var (start, end, noteNumber) in notes.Where(i => i.end > xMin && i.start < xMax))
                    {
                        var translatedNoteNum =
                            BardPlayDevice.GetNoteNumberTranslatedByTrack(noteNumber, trackInfo.Index) + 48;
                        drawList.AddRectFilled(
                            ImPlot.PlotToPixels(start, translatedNoteNum + 1),
                            ImPlot.PlotToPixels(end, translatedNoteNum),
                            noteColorRgb, 4);
                    }
                }

                foreach (var (trackName, color, _) in legendInfoList.OrderBy(i => i.index))
                {
                    ImPlot.SetNextLineStyle(color);
                    var f = double.NegativeInfinity;
                    ImPlot.PlotLine(trackName, ref f, 1);
                }
            }

            DrawCurrentPlayTime(drawList, timelinePos);
            if (ensembleTimelinePos is not null)
            {
                DrawEnsemblePlayTime(drawList, (double)ensembleTimelinePos);
            }
            ImPlot.PopPlotClipRect();

            ImPlot.EndPlot();
        }
    }
    private static bool IsGuitarProgram(byte programNumber) => programNumber is 27 or 28 or 29 or 30 or 31;

    private static unsafe bool TryGetFfxivInstrument(byte programNumber, out Instrument instrument)
    {
        var firstOrDefault = MidiBard.Instruments.FirstOrDefault(i => i.ProgramNumber == programNumber);
        instrument = firstOrDefault;
        return firstOrDefault != default;
    }

    private static void DrawCurrentPlayTime(ImDrawListPtr drawList, double timelinePos)
    {
        drawList.AddLine(
            ImPlot.PlotToPixels(timelinePos, ImPlot.GetPlotLimits().Y.Min),
            ImPlot.PlotToPixels(timelinePos, ImPlot.GetPlotLimits().Y.Max),
            ImGui.ColorConvertFloat4ToU32(ImGuiColors.DalamudRed),
            ImGuiHelpers.GlobalScale);
    }

    private static void DrawEnsemblePlayTime(ImDrawListPtr drawList, double timelinePos)
    {
        drawList.AddLine(
            ImPlot.PlotToPixels(timelinePos, ImPlot.GetPlotLimits().Y.Min),
            ImPlot.PlotToPixels(timelinePos, ImPlot.GetPlotLimits().Y.Max),
            ImGui.ColorConvertFloat4ToU32(ImGuiColors.DalamudYellow),
            ImGuiHelpers.GlobalScale);
    }

    public unsafe void RefreshPlotData()
    {
        if (!MidiBard.config.PlotTracks) return;
        Task.Run(() =>
        {
            try
            {
                if (MidiBard.CurrentPlayback?.TrackInfos == null)
                {
                    PluginLog.Debug("try RefreshPlotData but CurrentTracks is null");
                    return;
                }

                var tmap = MidiBard.CurrentPlayback.TempoMap;

                _plotData = MidiBard.CurrentPlayback.TrackChunks.Select((trackChunk, index) =>
                    {
                        var trackNotes = trackChunk.GetNotes()
                            .Select(j => (j.TimeAs<MetricTimeSpan>(tmap).GetTotalSeconds(),
                                j.EndTimeAs<MetricTimeSpan>(tmap).GetTotalSeconds(), (int)j.NoteNumber))
                            .ToArray();

                        return (MidiBard.CurrentPlayback.TrackInfos[index], notes: trackNotes);
                    })
                    .ToArray();

                setNextLimit = true;
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "error when refreshing plot data");
            }
        });
    }

    private static Dictionary<byte, uint> GetChannelColorPalette(byte[] allNoteChannels)
    {
        return allNoteChannels.OrderBy(i => i)
            .Select((channelNumber, index) => (channelNumber, color: HSVToRGB(index / (float)allNoteChannels.Length, 0.8f, 1)))
            .ToDictionary(tuple => (byte)tuple.channelNumber, tuple => ImGui.ColorConvertFloat4ToU32(tuple.color));
    }

    private (TrackInfo trackInfo, (double start, double end, int noteNumber)[] notes)[] _plotData;

    private string[] noteNames = Enumerable.Range(0, 128)
        .Select(i => i % 12 == 0 ? new Note(new SevenBitNumber((byte)i)).ToString() : string.Empty)
        .ToArray();

    private static unsafe T* Alloc<T>() where T : unmanaged
    {
        var allocHGlobal = (T*)Marshal.AllocHGlobal(sizeof(T));
        *allocHGlobal = new T();
        return allocHGlobal;
    }

    //private unsafe float rounding;
    //private unsafe int stride;
    //private unsafe double* height = Alloc<double>();
    //private unsafe double* shift = Alloc<double>();
    //private float[] valuex = null;
    //private float[] valuex2 = null;
    //private float[] valuey = null;
    //private float[] valuey2 = null;
    //private static bool setup = true;
}