using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.JobGauge;
using Dalamud.Game.ClientState.JobGauge.Enums;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Internal;
using Dalamud.Utility;
using ImGuiNET;
using ImGuiScene;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Interface.Internal.Windows
{
    /// <summary>
    /// Class responsible for drawing the data/debug window.
    /// </summary>
    internal class DataWindow : Window
    {
        private readonly string[] dataKindNames = Enum.GetNames(typeof(DataKind)).Select(k => k.Replace("_", " ")).ToArray();

        private bool wasReady;
        private string serverOpString;
        private DataKind currentKind;

        private bool drawCharas = false;
        private float maxCharaDrawDistance = 20;

        private string inputSig = string.Empty;
        private IntPtr sigResult = IntPtr.Zero;

        private string inputAddonName = string.Empty;
        private int inputAddonIndex;

        private IntPtr findAgentInterfacePtr;

        private bool resolveGameData = false;
        private bool resolveObjects = false;

        private UIDebug addonInspector = null;

        // IPC
        private ICallGateProvider<string, string> ipcPub;
        private ICallGateSubscriber<string, string> ipcSub;
        private string callGateResponse = string.Empty;

        // Toast fields
        private string inputTextToast = string.Empty;
        private int toastPosition = 0;
        private int toastSpeed = 0;
        private int questToastPosition = 0;
        private bool questToastSound = false;
        private int questToastIconId = 0;
        private bool questToastCheckmark = false;

        // Fly text fields
        private int flyActor;
        private FlyTextKind flyKind;
        private int flyVal1;
        private int flyVal2;
        private string flyText1 = string.Empty;
        private string flyText2 = string.Empty;
        private int flyIcon;
        private Vector4 flyColor = new(1, 0, 0, 1);

        // ImGui fields
        private string inputTexPath = string.Empty;
        private TextureWrap debugTex = null;
        private Vector2 inputTexUv0 = Vector2.Zero;
        private Vector2 inputTexUv1 = Vector2.One;
        private Vector4 inputTintCol = Vector4.One;
        private Vector2 inputTexScale = Vector2.Zero;

        private uint copyButtonIndex = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataWindow"/> class.
        /// </summary>
        public DataWindow()
            : base("Dalamud Data")
        {
            this.Size = new Vector2(500, 500);
            this.SizeCondition = ImGuiCond.FirstUseEver;

            this.RespectCloseHotkey = false;

            this.Load();
        }

        private enum DataKind
        {
            Server_OpCode,
            Address,
            Object_Table,
            Fate_Table,
            Font_Test,
            Party_List,
            Buddy_List,
            Plugin_IPC,
            Condition,
            Gauge,
            Command,
            Addon,
            Addon_Inspector,
            StartInfo,
            Target,
            Toast,
            FlyText,
            ImGui,
            Tex,
            KeyState,
            Gamepad,
        }

        /// <inheritdoc/>
        public override void OnOpen()
        {
        }

        /// <inheritdoc/>
        public override void OnClose()
        {
        }

        /// <summary>
        /// Set the DataKind dropdown menu.
        /// </summary>
        /// <param name="dataKind">Data kind name, can be lower and/or without spaces.</param>
        public void SetDataKind(string dataKind)
        {
            if (string.IsNullOrEmpty(dataKind))
                return;

            dataKind = dataKind switch
            {
                "ai" => "Addon Inspector",
                "at" => "Object Table",  // Actor Table
                "ot" => "Object Table",
                _ => dataKind,
            };

            dataKind = dataKind.Replace(" ", string.Empty).ToLower();

            var matched = Enum.GetValues<DataKind>()
                .Where(kind => Enum.GetName(kind).Replace("_", string.Empty).ToLower() == dataKind)
                .FirstOrDefault();

            if (matched != default)
            {
                this.currentKind = matched;
            }
            else
            {
                Service<ChatGui>.Get().PrintError($"/xldata: Invalid data type {dataKind}");
            }
        }

        /// <summary>
        /// Draw the window via ImGui.
        /// </summary>
        public override void Draw()
        {
            this.copyButtonIndex = 0;

            // Main window
            if (ImGui.Button("Force Reload"))
                this.Load();
            ImGui.SameLine();
            var copy = ImGui.Button("Copy all");
            ImGui.SameLine();

            var currentKindIndex = (int)this.currentKind;
            if (ImGui.Combo("Data kind", ref currentKindIndex, this.dataKindNames, this.dataKindNames.Length))
            {
                this.currentKind = (DataKind)currentKindIndex;
            }

            ImGui.Checkbox("Resolve GameData", ref this.resolveGameData);

            ImGui.BeginChild("scrolling", new Vector2(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar);

            if (copy)
                ImGui.LogToClipboard();

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

            try
            {
                if (this.wasReady)
                {
                    switch (this.currentKind)
                    {
                        case DataKind.Server_OpCode:
                            this.DrawServerOpCode();
                            break;

                        case DataKind.Address:
                            this.DrawAddress();
                            break;

                        case DataKind.Object_Table:
                            this.DrawObjectTable();
                            break;

                        case DataKind.Fate_Table:
                            this.DrawFateTable();
                            break;

                        case DataKind.Font_Test:
                            this.DrawFontTest();
                            break;

                        case DataKind.Party_List:
                            this.DrawPartyList();
                            break;

                        case DataKind.Buddy_List:
                            this.DrawBuddyList();
                            break;

                        case DataKind.Plugin_IPC:
                            this.DrawPluginIPC();
                            break;

                        case DataKind.Condition:
                            this.DrawCondition();
                            break;

                        case DataKind.Gauge:
                            this.DrawGauge();
                            break;

                        case DataKind.Command:
                            this.DrawCommand();
                            break;

                        case DataKind.Addon:
                            this.DrawAddon();
                            break;

                        case DataKind.Addon_Inspector:
                            this.DrawAddonInspector();
                            break;

                        case DataKind.StartInfo:
                            this.DrawStartInfo();
                            break;

                        case DataKind.Target:
                            this.DrawTarget();
                            break;

                        case DataKind.Toast:
                            this.DrawToast();
                            break;

                        case DataKind.FlyText:
                            this.DrawFlyText();
                            break;

                        case DataKind.ImGui:
                            this.DrawImGui();
                            break;

                        case DataKind.Tex:
                            this.DrawTex();
                            break;

                        case DataKind.KeyState:
                            this.DrawKeyState();
                            break;

                        case DataKind.Gamepad:
                            this.DrawGamepad();
                            break;
                    }
                }
                else
                {
                    ImGui.TextUnformatted("Data not ready.");
                }
            }
            catch (Exception ex)
            {
                ImGui.TextUnformatted(ex.ToString());
            }

            ImGui.PopStyleVar();

            ImGui.EndChild();
        }

        private void DrawServerOpCode()
        {
            ImGui.TextUnformatted(this.serverOpString);
        }

        private void DrawAddress()
        {
            ImGui.InputText(".text sig", ref this.inputSig, 400);
            if (ImGui.Button("Resolve"))
            {
                try
                {
                    var sigScanner = Service<SigScanner>.Get();
                    this.sigResult = sigScanner.ScanText(this.inputSig);
                }
                catch (KeyNotFoundException)
                {
                    this.sigResult = new IntPtr(-1);
                }
            }

            ImGui.Text($"Result: {this.sigResult.ToInt64():X}");
            ImGui.SameLine();
            if (ImGui.Button($"C{this.copyButtonIndex++}"))
                ImGui.SetClipboardText(this.sigResult.ToInt64().ToString("x"));

            foreach (var debugScannedValue in BaseAddressResolver.DebugScannedValues)
            {
                ImGui.TextUnformatted($"{debugScannedValue.Key}");
                foreach (var valueTuple in debugScannedValue.Value)
                {
                    ImGui.TextUnformatted(
                        $"      {valueTuple.ClassName} - 0x{valueTuple.Address.ToInt64():x}");
                    ImGui.SameLine();

                    if (ImGui.Button($"C##copyAddress{this.copyButtonIndex++}"))
                        ImGui.SetClipboardText(valueTuple.Address.ToInt64().ToString("x"));
                }
            }
        }

        private void DrawObjectTable()
        {
            var chatGui = Service<ChatGui>.Get();
            var clientState = Service<ClientState>.Get();
            var framework = Service<Framework>.Get();
            var gameGui = Service<GameGui>.Get();
            var objectTable = Service<ObjectTable>.Get();

            var stateString = string.Empty;

            if (clientState.LocalPlayer == null)
            {
                ImGui.TextUnformatted("LocalPlayer null.");
            }
            else
            {
                stateString += $"FrameworkBase: {framework.Address.BaseAddress.ToInt64():X}\n";
                stateString += $"ObjectTableLen: {objectTable.Length}\n";
                stateString += $"LocalPlayerName: {clientState.LocalPlayer.Name}\n";
                stateString += $"CurrentWorldName: {(this.resolveGameData ? clientState.LocalPlayer.CurrentWorld.GameData.Name : clientState.LocalPlayer.CurrentWorld.Id.ToString())}\n";
                stateString += $"HomeWorldName: {(this.resolveGameData ? clientState.LocalPlayer.HomeWorld.GameData.Name : clientState.LocalPlayer.HomeWorld.Id.ToString())}\n";
                stateString += $"LocalCID: {clientState.LocalContentId:X}\n";
                stateString += $"LastLinkedItem: {chatGui.LastLinkedItemId}\n";
                stateString += $"TerritoryType: {clientState.TerritoryType}\n\n";

                ImGui.TextUnformatted(stateString);

                ImGui.Checkbox("Draw characters on screen", ref this.drawCharas);
                ImGui.SliderFloat("Draw Distance", ref this.maxCharaDrawDistance, 2f, 40f);

                for (var i = 0; i < objectTable.Length; i++)
                {
                    var obj = objectTable[i];

                    if (obj == null)
                        continue;

                    this.PrintGameObject(obj, i.ToString());

                    if (this.drawCharas && gameGui.WorldToScreen(obj.Position, out var screenCoords))
                    {
                        // So, while WorldToScreen will return false if the point is off of game client screen, to
                        // to avoid performance issues, we have to manually determine if creating a window would
                        // produce a new viewport, and skip rendering it if so
                        var objectText = $"{obj.Address.ToInt64():X}:{obj.ObjectId:X}[{i}] - {obj.ObjectKind} - {obj.Name}";

                        var screenPos = ImGui.GetMainViewport().Pos;
                        var screenSize = ImGui.GetMainViewport().Size;

                        var windowSize = ImGui.CalcTextSize(objectText);

                        // Add some extra safety padding
                        windowSize.X += ImGui.GetStyle().WindowPadding.X + 10;
                        windowSize.Y += ImGui.GetStyle().WindowPadding.Y + 10;

                        if (screenCoords.X + windowSize.X > screenPos.X + screenSize.X ||
                            screenCoords.Y + windowSize.Y > screenPos.Y + screenSize.Y)
                            continue;

                        if (obj.YalmDistanceX > this.maxCharaDrawDistance)
                            continue;

                        ImGui.SetNextWindowPos(new Vector2(screenCoords.X, screenCoords.Y));

                        ImGui.SetNextWindowBgAlpha(Math.Max(1f - (obj.YalmDistanceX / this.maxCharaDrawDistance), 0.2f));
                        if (ImGui.Begin(
                                $"Actor{i}##ActorWindow{i}",
                                ImGuiWindowFlags.NoDecoration |
                                ImGuiWindowFlags.AlwaysAutoResize |
                                ImGuiWindowFlags.NoSavedSettings |
                                ImGuiWindowFlags.NoMove |
                                ImGuiWindowFlags.NoMouseInputs |
                                ImGuiWindowFlags.NoDocking |
                                ImGuiWindowFlags.NoFocusOnAppearing |
                                ImGuiWindowFlags.NoNav))
                            ImGui.Text(objectText);
                        ImGui.End();
                    }
                }
            }
        }

        private void DrawFateTable()
        {
            var fateTable = Service<FateTable>.Get();
            var framework = Service<Framework>.Get();

            var stateString = string.Empty;
            if (fateTable.Length == 0)
            {
                ImGui.TextUnformatted("No fates or data not ready.");
            }
            else
            {
                stateString += $"FrameworkBase: {framework.Address.BaseAddress.ToInt64():X}\n";
                stateString += $"FateTableLen: {fateTable.Length}\n";

                ImGui.TextUnformatted(stateString);

                for (var i = 0; i < fateTable.Length; i++)
                {
                    var fate = fateTable[i];
                    if (fate == null)
                        continue;

                    var fateString = $"{fate.Address.ToInt64():X}:[{i}]" +
                        $" - Lv.{fate.Level} {fate.Name} ({fate.Progress}%)" +
                        $" - X{fate.Position.X} Y{fate.Position.Y} Z{fate.Position.Z}" +
                        $" - Territory {(this.resolveGameData ? (fate.TerritoryType.GameData?.Name ?? fate.TerritoryType.Id.ToString()) : fate.TerritoryType.Id.ToString())}\n";

                    fateString += $"       StartTimeEpoch: {fate.StartTimeEpoch}" +
                        $" - Duration: {fate.Duration}" +
                        $" - State: {fate.State}" +
                        $" - GameData name: {(this.resolveGameData ? (fate.GameData?.Name ?? fate.FateId.ToString()) : fate.FateId.ToString())}";

                    ImGui.TextUnformatted(fateString);
                    ImGui.SameLine();
                    if (ImGui.Button("C"))
                    {
                        ImGui.SetClipboardText(fate.Address.ToString("X"));
                    }
                }
            }
        }

        readonly FontAwesomeIcon[] glyphs = new []{
            0xF000, 0xF001, 0xF002, 0xF004, 0xF005, 0xF007, 0xF008, 0xF009, 0xF00A, 0xF00B, 0xF00C, 0xF00D, 0xF00E, 0xF010, 0xF011, 0xF012, 0xF013, 0xF015,
            0xF017, 0xF018, 0xF019, 0xF01C, 0xF01E, 0xF021, 0xF022, 0xF023, 0xF024, 0xF025, 0xF026, 0xF027, 0xF028, 0xF029, 0xF02A, 0xF02B, 0xF02C, 0xF02D,
            0xF02E, 0xF02F, 0xF030, 0xF031, 0xF032, 0xF033, 0xF034, 0xF035, 0xF036, 0xF037, 0xF038, 0xF039, 0xF03A, 0xF03B, 0xF03C, 0xF03D, 0xF03E, 0xF041,
            0xF042, 0xF043, 0xF044, 0xF048, 0xF049, 0xF04A, 0xF04B, 0xF04C, 0xF04D, 0xF04E, 0xF050, 0xF051, 0xF052, 0xF053, 0xF054, 0xF055, 0xF056, 0xF057,
            0xF058, 0xF059, 0xF05A, 0xF05B, 0xF05E, 0xF060, 0xF061, 0xF062, 0xF063, 0xF064, 0xF065, 0xF066, 0xF067, 0xF068, 0xF069, 0xF06A, 0xF06B, 0xF06C,
            0xF06D, 0xF06E, 0xF070, 0xF071, 0xF072, 0xF073, 0xF074, 0xF075, 0xF076, 0xF077, 0xF078, 0xF079, 0xF07A, 0xF07B, 0xF07C, 0xF080, 0xF083, 0xF084,
            0xF085, 0xF086, 0xF089, 0xF08D, 0xF091, 0xF093, 0xF094, 0xF095, 0xF098, 0xF09C, 0xF09D, 0xF09E, 0xF0A0, 0xF0A1, 0xF0A3, 0xF0A4, 0xF0A5, 0xF0A6,
            0xF0A7, 0xF0A8, 0xF0A9, 0xF0AA, 0xF0AB, 0xF0AC, 0xF0AD, 0xF0AE, 0xF0B0, 0xF0B1, 0xF0B2, 0xF0C0, 0xF0C1, 0xF0C2, 0xF0C3, 0xF0C4, 0xF0C5, 0xF0C6,
            0xF0C7, 0xF0C8, 0xF0C9, 0xF0CA, 0xF0CB, 0xF0CC, 0xF0CD, 0xF0CE, 0xF0D0, 0xF0D1, 0xF0D6, 0xF0D7, 0xF0D8, 0xF0D9, 0xF0DA, 0xF0DB, 0xF0DC, 0xF0DD,
            0xF0DE, 0xF0E0, 0xF0E2, 0xF0E3, 0xF0E7, 0xF0E8, 0xF0E9, 0xF0EA, 0xF0EB, 0xF0F0, 0xF0F1, 0xF0F2, 0xF0F3, 0xF0F4, 0xF0F8, 0xF0F9, 0xF0FA, 0xF0FB,
            0xF0FC, 0xF0FD, 0xF0FE, 0xF100, 0xF101, 0xF102, 0xF103, 0xF104, 0xF105, 0xF106, 0xF107, 0xF108, 0xF109, 0xF10A, 0xF10B, 0xF10D, 0xF10E, 0xF110,
            0xF111, 0xF118, 0xF119, 0xF11A, 0xF11B, 0xF11C, 0xF11E, 0xF120, 0xF121, 0xF122, 0xF124, 0xF125, 0xF126, 0xF127, 0xF128, 0xF129, 0xF12A, 0xF12B,
            0xF12C, 0xF12D, 0xF12E, 0xF130, 0xF131, 0xF133, 0xF134, 0xF135, 0xF137, 0xF138, 0xF139, 0xF13A, 0xF13D, 0xF13E, 0xF140, 0xF141, 0xF142, 0xF143,
            0xF144, 0xF146, 0xF14A, 0xF14B, 0xF14D, 0xF14E, 0xF150, 0xF151, 0xF152, 0xF153, 0xF154, 0xF155, 0xF156, 0xF157, 0xF158, 0xF159, 0xF15B, 0xF15C,
            0xF15D, 0xF15E, 0xF160, 0xF161, 0xF162, 0xF163, 0xF164, 0xF165, 0xF182, 0xF183, 0xF185, 0xF186, 0xF187, 0xF188, 0xF191, 0xF192, 0xF193, 0xF195,
            0xF197, 0xF199, 0xF19C, 0xF19D, 0xF1AB, 0xF1AC, 0xF1AD, 0xF1AE, 0xF1B0, 0xF1B2, 0xF1B3, 0xF1B8, 0xF1B9, 0xF1BA, 0xF1BB, 0xF1C0, 0xF1C1, 0xF1C2,
            0xF1C3, 0xF1C4, 0xF1C5, 0xF1C6, 0xF1C7, 0xF1C8, 0xF1C9, 0xF1CD, 0xF1CE, 0xF1D8, 0xF1DA, 0xF1DC, 0xF1DD, 0xF1DE, 0xF1E0, 0xF1E1, 0xF1E2, 0xF1E3,
            0xF1E4, 0xF1E5, 0xF1E6, 0xF1EA, 0xF1EB, 0xF1EC, 0xF1F6, 0xF1F8, 0xF1F9, 0xF1FA, 0xF1FB, 0xF1FC, 0xF1FD, 0xF1FE, 0xF200, 0xF201, 0xF204, 0xF205,
            0xF206, 0xF207, 0xF20A, 0xF20B, 0xF217, 0xF218, 0xF21A, 0xF21B, 0xF21C, 0xF21D, 0xF21E, 0xF221, 0xF222, 0xF223, 0xF224, 0xF225, 0xF226, 0xF227,
            0xF228, 0xF229, 0xF22A, 0xF22B, 0xF22C, 0xF22D, 0xF233, 0xF234, 0xF235, 0xF236, 0xF238, 0xF239, 0xF240, 0xF241, 0xF242, 0xF243, 0xF244, 0xF245,
            0xF246, 0xF247, 0xF248, 0xF249, 0xF24D, 0xF24E, 0xF251, 0xF252, 0xF253, 0xF254, 0xF255, 0xF256, 0xF257, 0xF258, 0xF259, 0xF25A, 0xF25B, 0xF25C,
            0xF25D, 0xF26C, 0xF271, 0xF272, 0xF273, 0xF274, 0xF275, 0xF276, 0xF277, 0xF279, 0xF27A, 0xF28B, 0xF28D, 0xF290, 0xF291, 0xF292, 0xF295, 0xF29A,
            0xF29D, 0xF29E, 0xF2A0, 0xF2A1, 0xF2A2, 0xF2A3, 0xF2A4, 0xF2A7, 0xF2A8, 0xF2B5, 0xF2B6, 0xF2B9, 0xF2BB, 0xF2BD, 0xF2C1, 0xF2C2, 0xF2C7, 0xF2C8,
            0xF2C9, 0xF2CA, 0xF2CB, 0xF2CC, 0xF2CD, 0xF2CE, 0xF2D0, 0xF2D1, 0xF2D2, 0xF2DB, 0xF2DC, 0xF2E5, 0xF2E7, 0xF2EA, 0xF2ED, 0xF2F1, 0xF2F2, 0xF2F5,
            0xF2F6, 0xF2F9, 0xF2FE, 0xF302, 0xF303, 0xF304, 0xF305, 0xF309, 0xF30A, 0xF30B, 0xF30C, 0xF31E, 0xF328, 0xF337, 0xF338, 0xF358, 0xF359, 0xF35A,
            0xF35B, 0xF35D, 0xF360, 0xF362, 0xF381, 0xF382, 0xF3A5, 0xF3BE, 0xF3BF, 0xF3C1, 0xF3C5, 0xF3C9, 0xF3CD, 0xF3D1, 0xF3DD, 0xF3E0, 0xF3E5, 0xF3ED,
            0xF3FA, 0xF3FD, 0xF3FF, 0xF406, 0xF410, 0xF422, 0xF424, 0xF433, 0xF434, 0xF436, 0xF439, 0xF43A, 0xF43C, 0xF43F, 0xF441, 0xF443, 0xF445, 0xF447,
            0xF44B, 0xF44E, 0xF450, 0xF453, 0xF458, 0xF45C, 0xF45D, 0xF45F, 0xF461, 0xF462, 0xF466, 0xF468, 0xF469, 0xF46A, 0xF46B, 0xF46C, 0xF46D, 0xF470,
            0xF471, 0xF472, 0xF474, 0xF477, 0xF478, 0xF479, 0xF47D, 0xF47E, 0xF47F, 0xF481, 0xF482, 0xF484, 0xF485, 0xF486, 0xF487, 0xF48B, 0xF48D, 0xF48E,
            0xF490, 0xF491, 0xF492, 0xF493, 0xF494, 0xF496, 0xF497, 0xF49E, 0xF4AD, 0xF4B3, 0xF4B8, 0xF4B9, 0xF4BA, 0xF4BD, 0xF4BE, 0xF4C0, 0xF4C1, 0xF4C2,
            0xF4C4, 0xF4CD, 0xF4CE, 0xF4D3, 0xF4D6, 0xF4D7, 0xF4D8, 0xF4D9, 0xF4DA, 0xF4DB, 0xF4DE, 0xF4DF, 0xF4E2, 0xF4E3, 0xF4E6, 0xF4FA, 0xF4FB, 0xF4FC,
            0xF4FD, 0xF4FE, 0xF4FF, 0xF500, 0xF501, 0xF502, 0xF503, 0xF504, 0xF505, 0xF506, 0xF507, 0xF508, 0xF509, 0xF515, 0xF516, 0xF517, 0xF518, 0xF519,
            0xF51A, 0xF51B, 0xF51C, 0xF51D, 0xF51E, 0xF51F, 0xF520, 0xF521, 0xF522, 0xF523, 0xF524, 0xF525, 0xF526, 0xF527, 0xF528, 0xF529, 0xF52A, 0xF52B,
            0xF52C, 0xF52D, 0xF52E, 0xF52F, 0xF530, 0xF531, 0xF532, 0xF533, 0xF534, 0xF535, 0xF536, 0xF537, 0xF538, 0xF539, 0xF53A, 0xF53B, 0xF53C, 0xF53D,
            0xF53E, 0xF53F, 0xF540, 0xF541, 0xF542, 0xF543, 0xF544, 0xF545, 0xF546, 0xF547, 0xF548, 0xF549, 0xF54A, 0xF54B, 0xF54C, 0xF54D, 0xF54E, 0xF54F,
            0xF550, 0xF551, 0xF552, 0xF553, 0xF554, 0xF555, 0xF556, 0xF557, 0xF558, 0xF559, 0xF55A, 0xF55B, 0xF55C, 0xF55D, 0xF55E, 0xF55F, 0xF560, 0xF561,
            0xF562, 0xF563, 0xF564, 0xF565, 0xF566, 0xF567, 0xF568, 0xF569, 0xF56A, 0xF56B, 0xF56C, 0xF56D, 0xF56E, 0xF56F, 0xF570, 0xF571, 0xF572, 0xF573,
            0xF574, 0xF575, 0xF576, 0xF577, 0xF578, 0xF579, 0xF57A, 0xF57B, 0xF57C, 0xF57D, 0xF57E, 0xF57F, 0xF580, 0xF581, 0xF582, 0xF583, 0xF584, 0xF585,
            0xF586, 0xF587, 0xF588, 0xF589, 0xF58A, 0xF58B, 0xF58C, 0xF58D, 0xF58E, 0xF58F, 0xF590, 0xF591, 0xF593, 0xF594, 0xF595, 0xF596, 0xF597, 0xF598,
            0xF599, 0xF59A, 0xF59B, 0xF59C, 0xF59D, 0xF59F, 0xF5A0, 0xF5A1, 0xF5A2, 0xF5A4, 0xF5A5, 0xF5A6, 0xF5A7, 0xF5AA, 0xF5AB, 0xF5AC, 0xF5AD, 0xF5AE,
            0xF5AF, 0xF5B0, 0xF5B1, 0xF5B3, 0xF5B4, 0xF5B6, 0xF5B7, 0xF5B8, 0xF5BA, 0xF5BB, 0xF5BC, 0xF5BD, 0xF5BF, 0xF5C0, 0xF5C1, 0xF5C2, 0xF5C3, 0xF5C4,
            0xF5C5, 0xF5C7, 0xF5C8, 0xF5C9, 0xF5CA, 0xF5CB, 0xF5CD, 0xF5CE, 0xF5D0, 0xF5D1, 0xF5D2, 0xF5D7, 0xF5DA, 0xF5DC, 0xF5DE, 0xF5DF, 0xF5E1, 0xF5E4,
            0xF5E7, 0xF5EB, 0xF5EE, 0xF5FC, 0xF5FD, 0xF604, 0xF610, 0xF613, 0xF619, 0xF61F, 0xF621, 0xF62E, 0xF62F, 0xF630, 0xF637, 0xF63B, 0xF63C, 0xF641,
            0xF644, 0xF647, 0xF64A, 0xF64F, 0xF651, 0xF653, 0xF654, 0xF655, 0xF658, 0xF65D, 0xF65E, 0xF662, 0xF664, 0xF665, 0xF666, 0xF669, 0xF66A, 0xF66B,
            0xF66D, 0xF66F, 0xF674, 0xF676, 0xF678, 0xF679, 0xF67B, 0xF67C, 0xF67F, 0xF681, 0xF682, 0xF683, 0xF684, 0xF687, 0xF688, 0xF689, 0xF696, 0xF698,
            0xF699, 0xF69A, 0xF69B, 0xF6A0, 0xF6A1, 0xF6A7, 0xF6A9, 0xF6AD, 0xF6B6, 0xF6B7, 0xF6BB, 0xF6BE, 0xF6C0, 0xF6C3, 0xF6C4, 0xF6CF, 0xF6D1, 0xF6D3,
            0xF6D5, 0xF6D7, 0xF6D9, 0xF6DD, 0xF6DE, 0xF6E2, 0xF6E3, 0xF6E6, 0xF6E8, 0xF6EC, 0xF6ED, 0xF6F0, 0xF6F1, 0xF6F2, 0xF6FA, 0xF6FC, 0xF6FF, 0xF700,
            0xF70B, 0xF70C, 0xF70E, 0xF714, 0xF715, 0xF717, 0xF71E, 0xF722, 0xF728, 0xF729, 0xF72E, 0xF72F, 0xF73B, 0xF73C, 0xF73D, 0xF740, 0xF743, 0xF747,
            0xF74D, 0xF753, 0xF756, 0xF75A, 0xF75B, 0xF75E, 0xF75F, 0xF769, 0xF76B, 0xF772, 0xF773, 0xF77C, 0xF77D, 0xF780, 0xF781, 0xF783, 0xF784, 0xF786,
            0xF787, 0xF788, 0xF78C, 0xF793, 0xF794, 0xF796, 0xF79C, 0xF79F, 0xF7A0, 0xF7A2, 0xF7A4, 0xF7A5, 0xF7A6, 0xF7A9, 0xF7AA, 0xF7AB, 0xF7AD, 0xF7AE,
            0xF7B5, 0xF7B6, 0xF7B9, 0xF7BA, 0xF7BD, 0xF7BF, 0xF7C0, 0xF7C2, 0xF7C4, 0xF7C5, 0xF7C9, 0xF7CA, 0xF7CC, 0xF7CD, 0xF7CE, 0xF7D0, 0xF7D2, 0xF7D7,
            0xF7D8, 0xF7D9, 0xF7DA, 0xF7E4, 0xF7E5, 0xF7E6, 0xF7EC, 0xF7EF, 0xF7F2, 0xF7F5, 0xF7F7, 0xF7FA, 0xF7FB, 0xF805, 0xF806, 0xF807, 0xF80D, 0xF80F,
            0xF810, 0xF812, 0xF815, 0xF816, 0xF818, 0xF829, 0xF82A, 0xF82F, 0xF83E, 0xF84A, 0xF84C, 0xF850, 0xF853, 0xF863, 0xF86D, 0xF879, 0xF87B, 0xF87C,
            0xF87D, 0xF881, 0xF882, 0xF884, 0xF885, 0xF886, 0xF887, 0xF891, 0xF897, 0xF8C0, 0xF8C1, 0xF8CC, 0xF8D9, 0xF8FF, }
            .Select(i => (FontAwesomeIcon)i).ToArray();

        private void DrawFontTest()
        {
            var specialChars = string.Empty;

            for (var i = 0xE020; i <= 0xE0DB; i++)
                specialChars += $"0x{i:X} - {(SeIconChar)i} - {(char)i}\n";

            ImGui.TextUnformatted(specialChars);
            foreach (var fontAwesomeIcon in this.glyphs)
            {
                var s = fontAwesomeIcon.ToString();
                ImGui.TextUnformatted(s.Length > 15 ? s.Substring(0, 13) + "..." : s);
                ImGui.SameLine(110);
                ImGui.Text(((int)fontAwesomeIcon.ToIconChar()).ToString("X") + " - ");
                ImGui.SameLine();

                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.Text(fontAwesomeIcon.ToIconString());
                ImGui.PopFont();
            }
        }

        private void DrawPartyList()
        {
            var partyList = Service<PartyList>.Get();

            ImGui.Checkbox("Resolve Actors", ref this.resolveObjects);

            ImGui.Text($"GroupManager: {partyList.GroupManagerAddress.ToInt64():X}");
            ImGui.Text($"GroupList: {partyList.GroupListAddress.ToInt64():X}");
            ImGui.Text($"AllianceList: {partyList.AllianceListAddress.ToInt64():X}");

            ImGui.Text($"{partyList.Length} Members");

            for (var i = 0; i < partyList.Length; i++)
            {
                var member = partyList[i];
                if (member == null)
                {
                    ImGui.Text($"[{i}] was null");
                    continue;
                }

                ImGui.Text($"[{i}] {member.Address.ToInt64():X} - {member.Name} - {member.GameObject.ObjectId}");
                if (this.resolveObjects)
                {
                    var actor = member.GameObject;
                    if (actor == null)
                    {
                        ImGui.Text("Actor was null");
                    }
                    else
                    {
                        this.PrintGameObject(actor, "-");
                    }
                }
            }
        }

        private void DrawBuddyList()
        {
            var buddyList = Service<BuddyList>.Get();

            ImGui.Checkbox("Resolve Actors", ref this.resolveObjects);

            ImGui.Text($"BuddyList: {buddyList.BuddyListAddress.ToInt64():X}");
            {
                var member = buddyList.CompanionBuddy;
                if (member == null)
                {
                    ImGui.Text("[Companion] null");
                }
                else
                {
                    ImGui.Text($"[Companion] {member.Address.ToInt64():X} - {member.ObjectId} - {member.DataID}");
                    if (this.resolveObjects)
                    {
                        var gameObject = member.GameObject;
                        if (gameObject == null)
                        {
                            ImGui.Text("GameObject was null");
                        }
                        else
                        {
                            this.PrintGameObject(gameObject, "-");
                        }
                    }
                }
            }

            {
                var member = buddyList.PetBuddy;
                if (member == null)
                {
                    ImGui.Text("[Pet] null");
                }
                else
                {
                    ImGui.Text($"[Pet] {member.Address.ToInt64():X} - {member.ObjectId} - {member.DataID}");
                    if (this.resolveObjects)
                    {
                        var gameObject = member.GameObject;
                        if (gameObject == null)
                        {
                            ImGui.Text("GameObject was null");
                        }
                        else
                        {
                            this.PrintGameObject(gameObject, "-");
                        }
                    }
                }
            }

            {
                var count = buddyList.Length;
                if (count == 0)
                {
                    ImGui.Text("[BattleBuddy] None present");
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        var member = buddyList[i];
                        ImGui.Text($"[BattleBuddy] [{i}] {member.Address.ToInt64():X} - {member.ObjectId} - {member.DataID}");
                        if (this.resolveObjects)
                        {
                            var gameObject = member.GameObject;
                            if (gameObject == null)
                            {
                                ImGui.Text("GameObject was null");
                            }
                            else
                            {
                                this.PrintGameObject(gameObject, "-");
                            }
                        }
                    }
                }
            }
        }

        private void DrawPluginIPC()
        {
            if (this.ipcPub == null)
            {
                this.ipcPub = new CallGatePubSub<string, string>("dataDemo1");

                this.ipcPub.RegisterAction((msg) =>
                {
                    Log.Information($"Data action was called: {msg}");
                });

                this.ipcPub.RegisterFunc((msg) =>
                {
                    Log.Information($"Data func was called: {msg}");
                    return Guid.NewGuid().ToString();
                });
            }

            if (this.ipcSub == null)
            {
                this.ipcSub = new CallGatePubSub<string, string>("dataDemo1");
                this.ipcSub.Subscribe((msg) =>
                {
                    Log.Information("PONG1");
                });
                this.ipcSub.Subscribe((msg) =>
                {
                    Log.Information("PONG2");
                });
                this.ipcSub.Subscribe((msg) =>
                {
                    throw new Exception("PONG3");
                });
            }

            if (ImGui.Button("PING"))
            {
                this.ipcPub.SendMessage("PING");
            }

            if (ImGui.Button("Action"))
            {
                this.ipcSub.InvokeAction("button1");
            }

            if (ImGui.Button("Func"))
            {
                this.callGateResponse = this.ipcSub.InvokeFunc("button2");
            }

            if (!this.callGateResponse.IsNullOrEmpty())
                ImGui.Text($"Response: {this.callGateResponse}");
        }

        private void DrawCondition()
        {
            var condition = Service<Condition>.Get();

#if DEBUG
            ImGui.Text($"ptr: 0x{condition.Address.ToInt64():X}");
#endif

            ImGui.Text("Current Conditions:");
            ImGui.Separator();

            var didAny = false;

            for (var i = 0; i < Condition.MaxConditionEntries; i++)
            {
                var typedCondition = (ConditionFlag)i;
                var cond = condition[typedCondition];

                if (!cond) continue;

                didAny = true;

                ImGui.Text($"ID: {i} Enum: {typedCondition}");
            }

            if (!didAny)
                ImGui.Text("None. Talk to a shop NPC or visit a market board to find out more!!!!!!!");
        }

        private void DrawGauge()
        {
            var clientState = Service<ClientState>.Get();
            var jobGauges = Service<JobGauges>.Get();

            var player = clientState.LocalPlayer;
            if (player == null)
            {
                ImGui.Text("Player is not present");
                return;
            }

            var jobID = player.ClassJob.Id;
            if (jobID == 19)
            {
                var gauge = jobGauges.Get<PLDGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.OathGauge)}: {gauge.OathGauge}");
            }
            else if (jobID == 20)
            {
                var gauge = jobGauges.Get<MNKGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.Chakra)}: {gauge.Chakra}");
            }
            else if (jobID == 21)
            {
                var gauge = jobGauges.Get<WARGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.BeastGauge)}: {gauge.BeastGauge}");
            }
            else if (jobID == 22)
            {
                var gauge = jobGauges.Get<DRGGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.BOTDTimer)}: {gauge.BOTDTimer}");
                ImGui.Text($"{nameof(gauge.BOTDState)}: {gauge.BOTDState}");
                ImGui.Text($"{nameof(gauge.EyeCount)}: {gauge.EyeCount}");
            }
            else if (jobID == 23)
            {
                var gauge = jobGauges.Get<BRDGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.SongTimer)}: {gauge.SongTimer}");
                ImGui.Text($"{nameof(gauge.Repertoire)}: {gauge.Repertoire}");
                ImGui.Text($"{nameof(gauge.SoulVoice)}: {gauge.SoulVoice}");
                ImGui.Text($"{nameof(gauge.Song)}: {gauge.Song}");
            }
            else if (jobID == 24)
            {
                var gauge = jobGauges.Get<WHMGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.LilyTimer)}: {gauge.LilyTimer}");
                ImGui.Text($"{nameof(gauge.Lily)}: {gauge.Lily}");
                ImGui.Text($"{nameof(gauge.BloodLily)}: {gauge.BloodLily}");
            }
            else if (jobID == 25)
            {
                var gauge = jobGauges.Get<BLMGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.EnochianTimer)}: {gauge.EnochianTimer}");
                ImGui.Text($"{nameof(gauge.ElementTimeRemaining)}: {gauge.ElementTimeRemaining}");
                ImGui.Text($"{nameof(gauge.PolyglotStacks)}: {gauge.PolyglotStacks}");
                ImGui.Text($"{nameof(gauge.UmbralHearts)}: {gauge.UmbralHearts}");
                ImGui.Text($"{nameof(gauge.UmbralIceStacks)}: {gauge.UmbralIceStacks}");
                ImGui.Text($"{nameof(gauge.AstralFireStacks)}: {gauge.AstralFireStacks}");
                ImGui.Text($"{nameof(gauge.InUmbralIce)}: {gauge.InUmbralIce}");
                ImGui.Text($"{nameof(gauge.InAstralFire)}: {gauge.InAstralFire}");
                ImGui.Text($"{nameof(gauge.IsEnochianActive)}: {gauge.IsEnochianActive}");
            }
            else if (jobID == 27)
            {
                var gauge = jobGauges.Get<SMNGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.TimerRemaining)}: {gauge.TimerRemaining}");
                ImGui.Text($"{nameof(gauge.ReturnSummon)}: {gauge.ReturnSummon}");
                ImGui.Text($"{nameof(gauge.ReturnSummonGlam)}: {gauge.ReturnSummonGlam}");
                ImGui.Text($"{nameof(gauge.AetherFlags)}: {gauge.AetherFlags}");
                ImGui.Text($"{nameof(gauge.IsPhoenixReady)}: {gauge.IsPhoenixReady}");
                ImGui.Text($"{nameof(gauge.IsBahamutReady)}: {gauge.IsBahamutReady}");
                ImGui.Text($"{nameof(gauge.HasAetherflowStacks)}: {gauge.HasAetherflowStacks}");
            }
            else if (jobID == 28)
            {
                var gauge = jobGauges.Get<SCHGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.Aetherflow)}: {gauge.Aetherflow}");
                ImGui.Text($"{nameof(gauge.FairyGauge)}: {gauge.FairyGauge}");
                ImGui.Text($"{nameof(gauge.SeraphTimer)}: {gauge.SeraphTimer}");
                ImGui.Text($"{nameof(gauge.DismissedFairy)}: {gauge.DismissedFairy}");
            }
            else if (jobID == 30)
            {
                var gauge = jobGauges.Get<NINGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.HutonTimer)}: {gauge.HutonTimer}");
                ImGui.Text($"{nameof(gauge.Ninki)}: {gauge.Ninki}");
                ImGui.Text($"{nameof(gauge.HutonManualCasts)}: {gauge.HutonManualCasts}");
            }
            else if (jobID == 31)
            {
                var gauge = jobGauges.Get<MCHGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.OverheatTimeRemaining)}: {gauge.OverheatTimeRemaining}");
                ImGui.Text($"{nameof(gauge.SummonTimeRemaining)}: {gauge.SummonTimeRemaining}");
                ImGui.Text($"{nameof(gauge.Heat)}: {gauge.Heat}");
                ImGui.Text($"{nameof(gauge.Battery)}: {gauge.Battery}");
                ImGui.Text($"{nameof(gauge.LastSummonBatteryPower)}: {gauge.LastSummonBatteryPower}");
                ImGui.Text($"{nameof(gauge.IsOverheated)}: {gauge.IsOverheated}");
                ImGui.Text($"{nameof(gauge.IsRobotActive)}: {gauge.IsRobotActive}");
            }
            else if (jobID == 32)
            {
                var gauge = jobGauges.Get<DRKGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.Blood)}: {gauge.Blood}");
                ImGui.Text($"{nameof(gauge.DarksideTimeRemaining)}: {gauge.DarksideTimeRemaining}");
                ImGui.Text($"{nameof(gauge.ShadowTimeRemaining)}: {gauge.ShadowTimeRemaining}");
                ImGui.Text($"{nameof(gauge.HasDarkArts)}: {gauge.HasDarkArts}");
            }
            else if (jobID == 33)
            {
                var gauge = jobGauges.Get<ASTGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.DrawnCard)}: {gauge.DrawnCard}");
                foreach (var seal in Enum.GetValues(typeof(SealType)).Cast<SealType>())
                {
                    var sealName = Enum.GetName(typeof(SealType), seal);
                    ImGui.Text($"{nameof(gauge.ContainsSeal)}({sealName}): {gauge.ContainsSeal(seal)}");
                }
            }
            else if (jobID == 34)
            {
                var gauge = jobGauges.Get<SAMGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.Kenki)}: {gauge.Kenki}");
                ImGui.Text($"{nameof(gauge.MeditationStacks)}: {gauge.MeditationStacks}");
                ImGui.Text($"{nameof(gauge.Sen)}: {gauge.Sen}");
                ImGui.Text($"{nameof(gauge.HasSetsu)}: {gauge.HasSetsu}");
                ImGui.Text($"{nameof(gauge.HasGetsu)}: {gauge.HasGetsu}");
                ImGui.Text($"{nameof(gauge.HasKa)}: {gauge.HasKa}");
            }
            else if (jobID == 35)
            {
                var gauge = jobGauges.Get<RDMGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.WhiteMana)}: {gauge.WhiteMana}");
                ImGui.Text($"{nameof(gauge.BlackMana)}: {gauge.BlackMana}");
            }
            else if (jobID == 37)
            {
                var gauge = jobGauges.Get<GNBGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.Ammo)}: {gauge.Ammo}");
                ImGui.Text($"{nameof(gauge.MaxTimerDuration)}: {gauge.MaxTimerDuration}");
                ImGui.Text($"{nameof(gauge.AmmoComboStep)}: {gauge.AmmoComboStep}");
            }
            else if (jobID == 38)
            {
                var gauge = jobGauges.Get<DNCGauge>();
                ImGui.Text($"Address: 0x{gauge.Address.ToInt64():X}");
                ImGui.Text($"{nameof(gauge.Feathers)}: {gauge.Feathers}");
                ImGui.Text($"{nameof(gauge.Esprit)}: {gauge.Esprit}");
                ImGui.Text($"{nameof(gauge.CompletedSteps)}: {gauge.CompletedSteps}");
                ImGui.Text($"{nameof(gauge.NextStep)}: {gauge.NextStep}");
                ImGui.Text($"{nameof(gauge.IsDancing)}: {gauge.IsDancing}");
            }
            else
            {
                ImGui.Text("No supported gauge exists for this job.");
            }
        }

        private void DrawCommand()
        {
            var commandManager = Service<CommandManager>.Get();

            foreach (var command in commandManager.Commands)
            {
                ImGui.Text($"{command.Key}\n    -> {command.Value.HelpMessage}\n    -> In help: {command.Value.ShowInHelp}\n\n");
            }
        }

        private unsafe void DrawAddon()
        {
            var gameGui = Service<GameGui>.Get();

            ImGui.InputText("Addon name", ref this.inputAddonName, 256);
            ImGui.InputInt("Addon Index", ref this.inputAddonIndex);

            if (this.inputAddonName.IsNullOrEmpty())
                return;

            var address = gameGui.GetAddonByName(this.inputAddonName, this.inputAddonIndex);

            if (address == IntPtr.Zero)
            {
                ImGui.Text("Null");
                return;
            }

            var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)address;
            var name = MemoryHelper.ReadStringNullTerminated((IntPtr)addon->Name);
            ImGui.TextUnformatted($"{name} - 0x{address.ToInt64():x}\n    v:{addon->IsVisible} x:{addon->X} y:{addon->Y} s:{addon->Scale}, w:{addon->RootNode->Width}, h:{addon->RootNode->Height}");

            if (ImGui.Button("Find Agent"))
            {
                this.findAgentInterfacePtr = gameGui.FindAgentInterface(address);
            }

            if (this.findAgentInterfacePtr != IntPtr.Zero)
            {
                ImGui.TextUnformatted($"Agent: 0x{this.findAgentInterfacePtr.ToInt64():x}");
                ImGui.SameLine();

                if (ImGui.Button("C"))
                    ImGui.SetClipboardText(this.findAgentInterfacePtr.ToInt64().ToString("x"));
            }
        }

        private void DrawAddonInspector()
        {
            this.addonInspector ??= new UIDebug();
            this.addonInspector.Draw();
        }

        private void DrawStartInfo()
        {
            var startInfo = Service<DalamudStartInfo>.Get();

            ImGui.Text(JsonConvert.SerializeObject(startInfo, Formatting.Indented));
        }

        private void DrawTarget()
        {
            var clientState = Service<ClientState>.Get();
            var targetMgr = Service<TargetManager>.Get();

            if (targetMgr.Target != null)
            {
                this.PrintGameObject(targetMgr.Target, "CurrentTarget");

                ImGui.Text("Target");
                Util.ShowObject(targetMgr.Target);

                var tot = targetMgr.Target.TargetObject;
                if (tot != null)
                {
                    ImGuiHelpers.ScaledDummy(10);

                    ImGui.Text("ToT");
                    Util.ShowObject(tot);
                }

                ImGuiHelpers.ScaledDummy(10);
            }

            if (targetMgr.FocusTarget != null)
                this.PrintGameObject(targetMgr.FocusTarget, "FocusTarget");

            if (targetMgr.MouseOverTarget != null)
                this.PrintGameObject(targetMgr.MouseOverTarget, "MouseOverTarget");

            if (targetMgr.PreviousTarget != null)
                this.PrintGameObject(targetMgr.PreviousTarget, "PreviousTarget");

            if (targetMgr.SoftTarget != null)
                this.PrintGameObject(targetMgr.SoftTarget, "SoftTarget");

            if (ImGui.Button("Clear CT"))
                targetMgr.ClearTarget();

            if (ImGui.Button("Clear FT"))
                targetMgr.ClearFocusTarget();

            var localPlayer = clientState.LocalPlayer;

            if (localPlayer != null)
            {
                if (ImGui.Button("Set CT"))
                    targetMgr.SetTarget(localPlayer);

                if (ImGui.Button("Set FT"))
                    targetMgr.SetFocusTarget(localPlayer);
            }
            else
            {
                ImGui.Text("LocalPlayer is null.");
            }
        }

        private void DrawToast()
        {
            var toastGui = Service<ToastGui>.Get();

            ImGui.InputText("Toast text", ref this.inputTextToast, 200);

            ImGui.Combo("Toast Position", ref this.toastPosition, new[] { "Bottom", "Top", }, 2);
            ImGui.Combo("Toast Speed", ref this.toastSpeed, new[] { "Slow", "Fast", }, 2);
            ImGui.Combo("Quest Toast Position", ref this.questToastPosition, new[] { "Centre", "Right", "Left" }, 3);
            ImGui.Checkbox("Quest Checkmark", ref this.questToastCheckmark);
            ImGui.Checkbox("Quest Play Sound", ref this.questToastSound);
            ImGui.InputInt("Quest Icon ID", ref this.questToastIconId);

            ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

            if (ImGui.Button("Show toast"))
            {
                toastGui.ShowNormal(this.inputTextToast, new ToastOptions
                {
                    Position = (ToastPosition)this.toastPosition,
                    Speed = (ToastSpeed)this.toastSpeed,
                });
            }

            if (ImGui.Button("Show Quest toast"))
            {
                toastGui.ShowQuest(this.inputTextToast, new QuestToastOptions
                {
                    Position = (QuestToastPosition)this.questToastPosition,
                    DisplayCheckmark = this.questToastCheckmark,
                    IconId = (uint)this.questToastIconId,
                    PlaySound = this.questToastSound,
                });
            }

            if (ImGui.Button("Show Error toast"))
            {
                toastGui.ShowError(this.inputTextToast);
            }
        }

        private void DrawFlyText()
        {
            if (ImGui.BeginCombo("Kind", this.flyKind.ToString()))
            {
                var names = Enum.GetNames(typeof(FlyTextKind));
                for (var i = 0; i < names.Length; i++)
                {
                    if (ImGui.Selectable($"{names[i]} ({i})"))
                        this.flyKind = (FlyTextKind)i;
                }

                ImGui.EndCombo();
            }

            ImGui.InputText("Text1", ref this.flyText1, 200);
            ImGui.InputText("Text2", ref this.flyText2, 200);

            ImGui.InputInt("Val1", ref this.flyVal1);
            ImGui.InputInt("Val2", ref this.flyVal2);

            ImGui.InputInt("Icon ID", ref this.flyIcon);
            ImGui.ColorEdit4("Color", ref this.flyColor);
            ImGui.InputInt("Actor Index", ref this.flyActor);
            var sendColor = ImGui.ColorConvertFloat4ToU32(this.flyColor);

            if (ImGui.Button("Send"))
            {
                Service<FlyTextGui>.Get().AddFlyText(
                    this.flyKind,
                    unchecked((uint)this.flyActor),
                    unchecked((uint)this.flyVal1),
                    unchecked((uint)this.flyVal2),
                    this.flyText1,
                    this.flyText2,
                    sendColor,
                    unchecked((uint)this.flyIcon));
            }
        }

        private void DrawImGui()
        {
            var interfaceManager = Service<InterfaceManager>.Get();
            var notifications = Service<NotificationManager>.Get();

            ImGui.Text("Monitor count: " + ImGui.GetPlatformIO().Monitors.Size);
            ImGui.Text("OverrideGameCursor: " + interfaceManager.OverrideGameCursor);

            ImGui.Button("THIS IS A BUTTON###hoverTestButton");
            interfaceManager.OverrideGameCursor = !ImGui.IsItemHovered();

            ImGui.Separator();

            ImGui.TextUnformatted($"WindowSystem.TimeSinceLastAnyFocus: {WindowSystem.TimeSinceLastAnyFocus.TotalMilliseconds:0}ms");

            ImGui.Separator();

            if (ImGui.Button("Add random notification"))
            {
                var rand = new Random();

                var title = rand.Next(0, 5) switch
                {
                    0 => "This is a toast",
                    1 => "Truly, a toast",
                    2 => "I am testing this toast",
                    3 => "I hope this looks right",
                    4 => "Good stuff",
                    5 => "Nice",
                    _ => null,
                };

                var type = rand.Next(0, 4) switch
                {
                    0 => Notifications.NotificationType.Error,
                    1 => Notifications.NotificationType.Warning,
                    2 => Notifications.NotificationType.Info,
                    3 => Notifications.NotificationType.Success,
                    4 => Notifications.NotificationType.None,
                    _ => Notifications.NotificationType.None,
                };

                var text = "Bla bla bla bla bla bla bla bla bla bla bla.\nBla bla bla bla bla bla bla bla bla bla bla bla bla bla.";

                notifications.AddNotification(text, title, type);
            }
        }

        private void DrawTex()
        {
            var dataManager = Service<DataManager>.Get();

            ImGui.InputText("Tex Path", ref this.inputTexPath, 255);
            ImGui.InputFloat2("UV0", ref this.inputTexUv0);
            ImGui.InputFloat2("UV1", ref this.inputTexUv1);
            ImGui.InputFloat4("Tint", ref this.inputTintCol);
            ImGui.InputFloat2("Scale", ref this.inputTexScale);

            if (ImGui.Button("Load Tex"))
            {
                try
                {
                    this.debugTex = dataManager.GetImGuiTexture(this.inputTexPath);
                    this.inputTexScale = new Vector2(this.debugTex.Width, this.debugTex.Height);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not load tex.");
                }
            }

            ImGuiHelpers.ScaledDummy(10);

            if (this.debugTex != null)
            {
                ImGui.Image(this.debugTex.ImGuiHandle, this.inputTexScale, this.inputTexUv0, this.inputTexUv1, this.inputTintCol);
                ImGuiHelpers.ScaledDummy(5);
                Util.ShowObject(this.debugTex);
            }
        }

        private void DrawKeyState()
        {
            var keyState = Service<KeyState>.Get();

            ImGui.Columns(4);

            var i = 0;
            foreach (var vkCode in keyState.GetValidVirtualKeys())
            {
                var code = (int)vkCode;
                var value = keyState[code];

                ImGui.PushStyleColor(ImGuiCol.Text, value ? ImGuiColors.HealerGreen : ImGuiColors.DPSRed);

                ImGui.Text($"{vkCode} ({code})");

                ImGui.PopStyleColor();

                i++;
                if (i % 24 == 0)
                    ImGui.NextColumn();
            }

            ImGui.Columns(1);
        }

        private void DrawGamepad()
        {
            var gamepadState = Service<GamepadState>.Get();

            static void DrawHelper(string text, uint mask, Func<GamepadButtons, float> resolve)
            {
                ImGui.Text($"{text} {mask:X4}");
                ImGui.Text($"DPadLeft {resolve(GamepadButtons.DpadLeft)} " +
                           $"DPadUp {resolve(GamepadButtons.DpadUp)} " +
                           $"DPadRight {resolve(GamepadButtons.DpadRight)} " +
                           $"DPadDown {resolve(GamepadButtons.DpadDown)} ");
                ImGui.Text($"West {resolve(GamepadButtons.West)} " +
                           $"North {resolve(GamepadButtons.North)} " +
                           $"East {resolve(GamepadButtons.East)} " +
                           $"South {resolve(GamepadButtons.South)} ");
                ImGui.Text($"L1 {resolve(GamepadButtons.L1)} " +
                           $"L2 {resolve(GamepadButtons.L2)} " +
                           $"R1 {resolve(GamepadButtons.R1)} " +
                           $"R2 {resolve(GamepadButtons.R2)} ");
                ImGui.Text($"Select {resolve(GamepadButtons.Select)} " +
                           $"Start {resolve(GamepadButtons.Start)} " +
                           $"L3 {resolve(GamepadButtons.L3)} " +
                           $"R3 {resolve(GamepadButtons.R3)} ");
            }

            ImGui.Text($"GamepadInput 0x{gamepadState.GamepadInputAddress.ToInt64():X}");

#if DEBUG
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (ImGui.IsItemClicked())
                ImGui.SetClipboardText($"0x{gamepadState.GamepadInputAddress.ToInt64():X}");
#endif

            DrawHelper(
                "Buttons Raw",
                gamepadState.ButtonsRaw,
                gamepadState.Raw);
            DrawHelper(
                "Buttons Pressed",
                gamepadState.ButtonsPressed,
                gamepadState.Pressed);
            DrawHelper(
                "Buttons Repeat",
                gamepadState.ButtonsRepeat,
                gamepadState.Repeat);
            DrawHelper(
                "Buttons Released",
                gamepadState.ButtonsReleased,
                gamepadState.Released);
            ImGui.Text($"LeftStickLeft {gamepadState.LeftStickLeft:0.00} " +
                       $"LeftStickUp {gamepadState.LeftStickUp:0.00} " +
                       $"LeftStickRight {gamepadState.LeftStickRight:0.00} " +
                       $"LeftStickDown {gamepadState.LeftStickDown:0.00} ");
            ImGui.Text($"RightStickLeft {gamepadState.RightStickLeft:0.00} " +
                       $"RightStickUp {gamepadState.RightStickUp:0.00} " +
                       $"RightStickRight {gamepadState.RightStickRight:0.00} " +
                       $"RightStickDown {gamepadState.RightStickDown:0.00} ");
        }

        private void Load()
        {
            var dataManager = Service<DataManager>.Get();

            if (dataManager.IsDataReady)
            {
                this.serverOpString = JsonConvert.SerializeObject(dataManager.ServerOpCodes, Formatting.Indented);
                this.wasReady = true;
            }
        }

        private void PrintGameObject(GameObject actor, string tag)
        {
            var actorString =
                $"{actor.Address.ToInt64():X}:{actor.ObjectId:X}[{tag}] - {actor.ObjectKind} - {actor.Name} - X{actor.Position.X} Y{actor.Position.Y} Z{actor.Position.Z} D{actor.YalmDistanceX} R{actor.Rotation} - Target: {actor.TargetObjectId:X}\n";

            if (actor is Npc npc)
                actorString += $"       DataId: {npc.DataId}  NameId:{npc.NameId}\n";

            if (actor is Character chara)
            {
                actorString +=
                    $"       Level: {chara.Level} ClassJob: {(this.resolveGameData ? chara.ClassJob.GameData.Name : chara.ClassJob.Id.ToString())} CHP: {chara.CurrentHp} MHP: {chara.MaxHp} CMP: {chara.CurrentMp} MMP: {chara.MaxMp}\n       Customize: {BitConverter.ToString(chara.Customize).Replace("-", " ")} StatusFlags: {chara.StatusFlags}\n";
            }

            if (actor is PlayerCharacter pc)
            {
                actorString +=
                    $"       HomeWorld: {(this.resolveGameData ? pc.HomeWorld.GameData.Name : pc.HomeWorld.Id.ToString())} CurrentWorld: {(this.resolveGameData ? pc.CurrentWorld.GameData.Name : pc.CurrentWorld.Id.ToString())} FC: {pc.CompanyTag}\n";
            }

            ImGui.TextUnformatted(actorString);
            ImGui.SameLine();
            if (ImGui.Button($"C##{this.copyButtonIndex++}"))
            {
                ImGui.SetClipboardText(actor.Address.ToInt64().ToString("X"));
            }
        }
    }
}
