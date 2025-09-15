using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using NekoNetClient.FileCache;
using NekoNetClient.Localization;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.NekoHelpers;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.WebAPI.SignalR;
using NekoNetClient.Services.Sync;
using System.Numerics;
using System.Text.RegularExpressions;

namespace NekoNetClient.UI;

public partial class IntroUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly Dictionary<string, string> _languages = new(StringComparer.Ordinal) { { "English", "en" }, { "Deutsch", "de" }, { "Français", "fr" } };
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly UiSharedService _uiShared;
    private readonly FileDialogManager _fileDialogManager;
    // Holds folders found by "Auto-detect Mare Folders"
    private readonly List<string> _autoDetected = new();
    // Holds results for multi-source import
    private readonly List<ImportWizard.ScanResult> _scanList = new();
    // Selection state for auto-detected folders
    private readonly Dictionary<string, bool> _autoDetectedChecked = new(StringComparer.OrdinalIgnoreCase);

    private void UpdateSuggestedCacheFromScans()
    {
        try
        {
            var best = _scanList
                .Select(sc => new { Scan = sc, Path = sc.SuggestedCacheFolder ?? string.Empty, Time = new DirectoryInfo(sc.SourceFolder).LastWriteTimeUtc })
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .OrderByDescending(x => x.Time)
                .FirstOrDefault();

            if (best != null)
            {
                _cachePathOverride = best.Path;
                _reuseCache = best.Scan.FileCacheExists && !string.IsNullOrWhiteSpace(_cachePathOverride);
            }
        }
        catch
        {
            // ignore issues computing suggestion
        }
    }

    private int _currentLanguage;
    private bool _readFirstPage;
    private bool _showImportModal;
    private string _importPath = "";
    private ImportWizard.ScanResult? _scan;
    private string _cachePathOverride = "";
    private bool _reuseCache;
    private bool _isImporting = false;
    //private bool _shouldOpenFileDialog = false; // Added this field

    private string _secretKey = string.Empty;
    private string _timeoutLabel = string.Empty;
    private Task? _timeoutTask;
    private string[]? _tosParagraphs;
    private bool _useLegacyLogin = false;

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, MareConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, MareMediator mareMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService,
        FileDialogManager fileDialogManager) : base(logger, mareMediator, "Neko-Net Setup", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(600, 2000),
        };

        GetToSLocalization();

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) =>
        {
            _configService.Current.UseCompactor = !dalamudUtilService.IsWine;
            IsOpen = true;
        });
    }

    private int _prevIdx = -1;

    private void OpenFolderDialog()
    {
        _fileDialogManager.OpenFolderDialog("Select Mare/OpenSynchronos Config Folder", (success, path) =>
        {
            if (success && Directory.Exists(path))
            {
                _importPath = path;
            }
        }, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    private void OpenCacheFolderDialog()
    {
        _fileDialogManager.OpenFolderDialog("Select Cache Folder", (success, path) =>
        {
            if (success && Directory.Exists(path))
            {
                _cachePathOverride = path;
            }
        }, string.IsNullOrEmpty(_cachePathOverride) ?
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) :
            Path.GetDirectoryName(_cachePathOverride));
    }

    protected override void DrawInternal()
    {

        if (_uiShared.IsInGpose) return;

        if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
        {
            // Redesigned first page with cleaner layout and multi-config import
            _uiShared.BigText("Welcome to Neko-Net");
            ImGui.Separator();
            using (var table = ImRaii.Table("intro_layout_table", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
            {
                if (table)
                {
                    // Left: description and requirements
                    ImGui.TableNextColumn();
                    UiSharedService.TextWrapped("Neko-Net replicates your full current character state, including all Penumbra mods, to other paired Neko-Net users.");
                    UiSharedService.TextWrapped("Penumbra and Glamourer are required to use this plugin.");
                    UiSharedService.TextWrapped("We’ll guide you through a quick setup to get started.");
                    ImGuiHelpers.ScaledDummy(6f);
                    UiSharedService.ColorTextWrapped("Note: Changes applied with tools other than Penumbra cannot be shared reliably. For best results, move your mods to Penumbra.", ImGuiColors.DalamudYellow);
                    ImGuiHelpers.ScaledDummy(6f);
                    if (!_uiShared.DrawOtherPluginState()) return;

                    // Right: quick actions
                    ImGui.TableNextColumn();
                    UiSharedService.DrawGrouped(() =>
                    {
                        ImGui.TextUnformatted("Get Started");
                        ImGui.Separator();
                        if (_uiShared.IconTextButton(FontAwesomeIcon.FolderOpen, "Import from Folder..."))
                        {
                            _showImportModal = true;
                            _importPath = string.Empty;
                            _scan = null;
                            _autoDetected.Clear();
                        }
                        UiSharedService.AttachToolTip("Add one or more config folders and import selected servers.");
                        ImGuiHelpers.ScaledDummy(4f);
                        if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowRight, "Next"))
                        {
                            _readFirstPage = true;
#if !DEBUG
                            _timeoutTask = Task.Run(async () =>
                            {
                                for (int i = 60; i > 0; i--)
                                {
                                    _timeoutLabel = $"{Strings.ToS.ButtonWillBeAvailableIn} {i}s";
                                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                                }
                            });
#else
                            _timeoutTask = Task.CompletedTask;
#endif
                        }
                    }, rounding: 6f, expectedWidth: 260f);
                }
            }

            // Multi-source Import UI (non-modal window)
            if (_showImportModal)
            {
                if (ImGui.IsWindowAppearing()) ImGui.SetNextWindowFocus();
                ImGui.SetNextWindowSizeConstraints(new Vector2(620, 0), new Vector2(900, 1200));
                if (ImGui.Begin("Import from Mare/OpenSynchronos", ref _showImportModal,
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
                {
                    var wizard = new ImportWizard(_configService, _serverConfigurationManager);
                    // Quick scan on open if list is empty
                    if (_autoDetected.Count == 0)
                    {
                        var appDataInit = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var pluginConfigsInit = Path.Combine(appDataInit, "XIVLauncher", "pluginConfigs");
                        if (Directory.Exists(pluginConfigsInit))
                        {
                            _autoDetected.Clear();
                            foreach (var dir in Directory.GetDirectories(pluginConfigsInit)
                                         .Where(d => {
                                             var name = Path.GetFileName(d).ToLowerInvariant();
                                             return name.Contains("mare") || name.Contains("synchronos") || name.Contains("neko");
                                         })
                                         .Where(d => File.Exists(Path.Combine(d, "server.json")))
                                         .OrderByDescending(d => new DirectoryInfo(d).LastWriteTimeUtc))
                            {
                                _autoDetected.Add(dir);
                                if (!_autoDetectedChecked.ContainsKey(dir)) _autoDetectedChecked[dir] = true;
                            }
                            // remove stale entries
                            foreach (var key in _autoDetectedChecked.Keys.ToList())
                                if (!_autoDetected.Contains(key)) _autoDetectedChecked.Remove(key);
                        }
                        // After first populate, try to choose suggested cache
                        if (_scanList.Count == 0)
                        {
                            // perform lightweight scans without reading files to avoid delays? we already need server.json existence; call ScanFolder for each preselected.
                            foreach (var dir in _autoDetected)
                            {
                                try
                                {
                                    var sc = wizard.ScanFolder(dir);
                                    if (string.IsNullOrEmpty(sc.Error))
                                    {
                                        // do not add yet; only use to pick cache suggestion
                                        if (string.IsNullOrEmpty(_cachePathOverride) && !string.IsNullOrWhiteSpace(sc.SuggestedCacheFolder))
                                            _cachePathOverride = sc.SuggestedCacheFolder;
                                        if (sc.FileCacheExists && !string.IsNullOrWhiteSpace(_cachePathOverride)) _reuseCache = true;
                                    }
                                }
                                catch { /* ignore */ }
                            }
                        }
                    }

                    ImGui.TextUnformatted("Add source folder(s) containing server.json (and optionally config.json / FileCache.csv)");
                    ImGui.SetNextItemWidth(400);
                    ImGui.InputTextWithHint("##importpath", "Select folder to add", ref _importPath);
                    ImGui.SameLine();
                    if (ImGui.Button("Browse..."))
                        OpenFolderDialog();
                    ImGui.SameLine();
                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_importPath)))
                    {
                        if (ImGui.Button("Add"))
                        {
                            try
                            {
                                var scan = wizard.ScanFolder(_importPath);
                                if (string.IsNullOrEmpty(scan.Error))
                                {
                                    _scanList.Add(scan);
                                    UpdateSuggestedCacheFromScans();
                                }
                                else
                                {
                                    _scan = scan; // keep last error visible
                                }
                            }
                            catch (Exception ex)
                            {
                                _scan = new ImportWizard.ScanResult { SourceFolder = _importPath, Error = $"Scan failed: {ex.Message}" };
                            }
                        }
                    }

                    ImGui.TextUnformatted("Quick presets:");
                    if (ImGui.Button("Mare Synchronos"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "MareSynchronos");
                    ImGui.SameLine();
                    if (ImGui.Button("OpenSynchronos"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "OpenSynchronos");
                    ImGui.SameLine();
                    if (ImGui.Button("Neko-Net"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "NekoNetClient");
                    ImGui.SameLine();
                    if (ImGui.Button("Tera Sync"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "TeraSyncV2");
                    ImGui.SameLine();
                    if (ImGui.Button("Lightless Sync"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "LightlessSync");

                    if (ImGui.Button("Quick Scan"))
                    {
                        _autoDetected.Clear();
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var pluginConfigs = Path.Combine(appData, "XIVLauncher", "pluginConfigs");
                        if (Directory.Exists(pluginConfigs))
                        {
                            foreach (var dir in Directory.GetDirectories(pluginConfigs)
                                         .Where(d => {
                                             var name = Path.GetFileName(d).ToLowerInvariant();
                                             return name.Contains("mare") || name.Contains("synchronos") || name.Contains("neko");
                                         })
                                         .Where(d => File.Exists(Path.Combine(d, "server.json")))
                                         .OrderByDescending(d => new DirectoryInfo(d).LastWriteTimeUtc))
                            {
                                _autoDetected.Add(dir);
                                if (!_autoDetectedChecked.ContainsKey(dir)) _autoDetectedChecked[dir] = true;
                            }
                            // cleanup stale
                            foreach (var key in _autoDetectedChecked.Keys.ToList())
                                if (!_autoDetected.Contains(key)) _autoDetectedChecked.Remove(key);
                        }
                    }

                    if (_autoDetected is { Count: > 0 })
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted("Found Mare-like folders (check and Add Selected):");
                        ImGui.BeginChild("autodetect_list", new Vector2(0, 140), true);
                        foreach (var folder in _autoDetected)
                        {
                            var name = Path.GetFileName(folder);
                            var last = new DirectoryInfo(folder).LastWriteTimeUtc;
                            var checkedVal = _autoDetectedChecked.TryGetValue(folder, out var v) && v;
                            if (ImGui.Checkbox($"{name} (modified: {last:yyyy-MM-dd})", ref checkedVal))
                                _autoDetectedChecked[folder] = checkedVal;
                        }
                        ImGui.EndChild();
                        // Actions for detected list
                        if (ImGui.SmallButton("Select All"))
                        {
                            foreach (var k in _autoDetected.ToList()) _autoDetectedChecked[k] = true;
                        }
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Clear"))
                        {
                            foreach (var k in _autoDetected.ToList()) _autoDetectedChecked[k] = false;
                        }
                        ImGui.SameLine();
                        using (ImRaii.Disabled(!_autoDetected.Any(p => _autoDetectedChecked.TryGetValue(p, out var b) && b)))
                        {
                            if (ImGui.SmallButton("Add Selected"))
                            {
                                string? err = null;
                                foreach (var folder in _autoDetected.Where(p => _autoDetectedChecked.TryGetValue(p, out var b) && b))
                                {
                                    if (_scanList.Any(s => string.Equals(s.SourceFolder, folder, StringComparison.OrdinalIgnoreCase))) continue;
                                    try
                                    {
                                        var sc = wizard.ScanFolder(folder);
                                        if (!string.IsNullOrEmpty(sc.Error)) err = (err == null ? "" : err + "\n") + $"{Path.GetFileName(folder)}: {sc.Error}"; else _scanList.Add(sc);
                                    }
                                    catch (Exception ex)
                                    {
                                        err = (err == null ? "" : err + "\n") + $"{Path.GetFileName(folder)}: {ex.Message}";
                                    }
                                }
                                UpdateSuggestedCacheFromScans();
                                _scan = string.IsNullOrEmpty(err) ? null : new ImportWizard.ScanResult { Error = "Add failed for some folders:\n" + err };
                            }
                        }
                    }

                    ImGui.Separator();

                    if (_scan != null && !string.IsNullOrEmpty(_scan.Error))
                    {
                        UiSharedService.ColorTextWrapped(_scan.Error, ImGuiColors.DalamudRed);
                    }

                    if (_scanList.Count > 0)
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted("Added sources:");
                        if (ImGui.BeginTable("added_sources", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
                        {
                            ImGui.TableSetupColumn("Folder");
                            ImGui.TableSetupColumn("Servers", ImGuiTableColumnFlags.WidthFixed, 70);
                            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 80);
                            ImGui.TableHeadersRow();
                            for (int s = 0; s < _scanList.Count; s++)
                            {
                                var sc = _scanList[s];
                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0);
                                ImGui.TextUnformatted(sc.SourceFolder);
                                ImGui.TableSetColumnIndex(1);
                                ImGui.TextUnformatted(sc.SourceServers.Count.ToString());
                                ImGui.TableSetColumnIndex(2);
                                if (ImGui.SmallButton($"Remove##src_{s}"))
                                {
                                    _scanList.RemoveAt(s);
                                    s--; continue;
                                }
                            }
                            ImGui.EndTable();
                        }

                        ImGui.Separator();
                        ImGui.TextUnformatted("Cache folder to use:");
                        ImGui.SetNextItemWidth(300);
                        ImGui.InputTextWithHint("##cachePath", "Choose a cache folder (optional)", ref _cachePathOverride);
                        ImGui.SameLine();
                        if (ImGui.Button("Browse##cache")) OpenCacheFolderDialog();
                        var anyHasFileCache = _scanList.Any(s => s.FileCacheExists);
                        if (anyHasFileCache)
                            ImGui.Checkbox("Reuse existing FileCache.csv (skip re-scan)", ref _reuseCache);

                        ImGui.Separator();
                        using (var table2 = ImRaii.Table("import_servers_table", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                        {
                            if (table2)
                            {
                                ImGui.TableSetupColumn("Import", ImGuiTableColumnFlags.WidthFixed, 60);
                                ImGui.TableSetupColumn("Name");
                                ImGui.TableSetupColumn("URI");
                                ImGui.TableSetupColumn("Auth");
                                ImGui.TableSetupColumn("OAuth");
                                ImGui.TableSetupColumn("Source");
                                ImGui.TableHeadersRow();
                                for (int s = 0; s < _scanList.Count; s++)
                                {
                                    var sc = _scanList[s];
                                    for (int i = 0; i < sc.SourceServers.Count; i++)
                                    {
                                        var row = sc.SourceServers[i];
                                        ImGui.TableNextRow();
                                        ImGui.TableSetColumnIndex(0);
                                        var sel = row.Selected;
                                        if (ImGui.Checkbox($"##sel_{s}_{i}", ref sel)) row.Selected = sel;
                                        ImGui.TableSetColumnIndex(1);
                                        if (i == sc.SourceCurrentIndex)
                                            UiSharedService.ColorText(row.ServerName + " (current)", ImGuiColors.HealerGreen);
                                        else
                                            ImGui.TextUnformatted(row.ServerName);
                                        ImGui.TableSetColumnIndex(2);
                                        ImGui.TextUnformatted(row.ServerUri);
                                        ImGui.TableSetColumnIndex(3);
                                        ImGui.TextUnformatted(row.AuthCount.ToString());
                                        ImGui.TableSetColumnIndex(4);
                                        ImGui.TextUnformatted(row.UseOAuth2 ? (row.HasToken ? "OAuth2 ✓" : "OAuth2") : "SecretKey");
                                        ImGui.TableSetColumnIndex(5);
                                        ImGui.TextUnformatted(Path.GetFileName(sc.SourceFolder));
                                    }
                                }
                            }
                        }

                        ImGui.Separator();
                        var selectedCount = _scanList.Sum(sc => sc.SourceServers.Count(s => s.Selected));
                        using (ImRaii.Disabled(selectedCount == 0))
                        {
                            if (ImGui.Button($"Import Selected ({selectedCount})"))
                            {
                                int created = 0, updated = 0; bool cacheConfigured = false; bool cacheReused = false; string? err = null;
                                bool first = true;
                                foreach (var sc in _scanList)
                                {
                                    var chosen = sc.SourceServers.Where(s => s.Selected).ToList();
                                    if (chosen.Count == 0) continue;
                                    var sum = wizard.ImportSelected(sc, chosen, first ? _cachePathOverride : null, first ? _reuseCache : false);
                                    if (!string.IsNullOrEmpty(sum.Error)) { err = sum.Error; break; }
                                    created += sum.Created; updated += sum.Updated; cacheConfigured |= sum.CacheConfigured; cacheReused |= sum.CacheReused; first = false;
                                }
                                if (!string.IsNullOrEmpty(err))
                                {
                                    UiSharedService.ColorTextWrapped(err!, ImGuiColors.DalamudRed);
                                }
                                else
                                {
                                    UiSharedService.ColorTextWrapped($"Successfully imported {created + updated} server(s)!\n- Created: {created} new\n- Updated: {updated} existing\n- Cache: {(cacheConfigured ? (_reuseCache ? "reused (skipped scan)" : "configured for new scan") : "unchanged")}", ImGuiColors.HealerGreen);
                                    _showImportModal = false;
                                    _readFirstPage = true;
#if !DEBUG
                                    _timeoutTask = Task.Run(async () =>
                                    {
                                        for (int i = 2; i > 0; i--)
                                        {
                                            _timeoutLabel = $"Continuing in {i}s";
                                            await Task.Delay(1000);
                                        }
                                    });
#else
                                    _timeoutTask = Task.CompletedTask;
#endif
                                }
                            }
                        }
                    }

                    if (ImGui.Button("Close")) _showImportModal = false;
                    ImGui.End();
                }
            }

            // Always draw file dialog manager for callbacks
            _fileDialogManager.Draw();
            return;
            _uiShared.BigText("Welcome to Neko-Net");
            ImGui.Separator();
            UiSharedService.TextWrapped("Neko-Net is a plugin that will replicate your full current character state including all Penumbra mods to other paired Neko-Net users. " +
                              "Note that you will have to have Penumbra as well as Glamourer installed to use this plugin.");
            UiSharedService.TextWrapped("We will have to setup a few things first before you can start using this plugin. Click on next to continue.");

            UiSharedService.ColorTextWrapped("Note: Any modifications you have applied through anything but Penumbra cannot be shared and your character state on other clients " +
                                 "might look broken because of this or others players mods might not apply on your end altogether. " +
                                 "If you want to use this plugin you will have to move your mods to Penumbra.", ImGuiColors.DalamudYellow);
            if (!_uiShared.DrawOtherPluginState()) return;
            ImGui.Separator();

            // Enhanced Import from Folder button
            if (ImGui.Button("Import from Folder…"))
            {
                _showImportModal = true;
                _importPath = "";
                _scan = null;
                _autoDetected.Clear();   
                // ImGui.OpenPopup("Import from Mare/OpenSynchronos"); // remove this
            }


            ImGui.SameLine();
            UiSharedService.AttachToolTip("Pick a prior Mare/OpenSynchronos/NekoNetClient config folder, choose servers to import, and optionally reuse its cache.");

            // Enhanced Import Modal
            // Enhanced Import "window" (non-modal)
            if (_showImportModal)
            {
                // Optional: center on first open
                if (ImGui.IsWindowAppearing())
                    ImGui.SetNextWindowFocus();

                ImGui.SetNextWindowSizeConstraints(new Vector2(620, 0), new Vector2(900, 1200));
                if (ImGui.Begin("Import from Mare/OpenSynchronos", ref _showImportModal,
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
                {
                    var wizard = new ImportWizard(_configService, _serverConfigurationManager);

                    ImGui.TextUnformatted("Source folder containing server.json (and optionally config.json / FileCache.csv)");

                    ImGui.SetNextItemWidth(400);
                    ImGui.InputTextWithHint("##importpath", "Select folder containing Mare/OpenSynchronos config files", ref _importPath);

                    ImGui.SameLine();
                    if (ImGui.Button("Browse..."))
                    {
                        // Call the file dialog *directly* now (no deferral flag needed)
                        OpenFolderDialog();
                    }

                    ImGui.TextUnformatted("Quick presets:");
                    if (ImGui.Button("Mare Synchronos"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "MareSynchronos");
                    ImGui.SameLine();
                    if (ImGui.Button("OpenSynchronos"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "OpenSynchronos");
                    ImGui.SameLine();
                    if (ImGui.Button("Neko-Net"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "NekoNetClient");
                    ImGui.SameLine();
                    if (ImGui.Button("Tera Sync"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "TeraSyncV2");
                    ImGui.SameLine();
                    if (ImGui.Button("Lightless Sync"))
                        _importPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "XIVLauncher", "pluginConfigs", "LightlessSync");

                    // Inline auto-detect results instead of another popup (keeps things non-modal)
                    if (ImGui.Button("Auto-detect Mare Folders"))
                    {
                        _autoDetected.Clear();
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        var pluginConfigs = Path.Combine(appData, "XIVLauncher", "pluginConfigs");
                        if (Directory.Exists(pluginConfigs))
                        {
                            _autoDetected.AddRange(
                                Directory.GetDirectories(pluginConfigs)
                                    .Where(dir =>
                                    {
                                        var name = Path.GetFileName(dir).ToLowerInvariant();
                                        return name.Contains("mare") || name.Contains("synchronos") || name.Contains("neko");
                                    })
                                    .Where(dir => File.Exists(Path.Combine(dir, "server.json")))
                                    .OrderByDescending(dir => new DirectoryInfo(dir).LastWriteTimeUtc)
                            );
                        }
                    }


                    // Optional: keep a field List<string> _autoDetected = new();
                    if (_autoDetected is { Count: > 0 })
                    {
                        ImGui.Separator();
                        ImGui.TextUnformatted("Found Mare-like folders:");
                        ImGui.BeginChild("autodetect_list", new Vector2(0, 120), true);
                        foreach (var folder in _autoDetected)
                        {
                            var name = Path.GetFileName(folder);
                            var last = new DirectoryInfo(folder).LastWriteTimeUtc;
                            if (ImGui.Selectable($"{name} (modified: {last:yyyy-MM-dd})"))
                                _importPath = folder;
                        }
                        ImGui.EndChild();
                    }

                    ImGui.Separator();

                    if (!string.IsNullOrWhiteSpace(_importPath))
                    {
                        if (!Directory.Exists(_importPath))
                            UiSharedService.ColorTextWrapped("Folder does not exist!", ImGuiColors.DalamudRed);
                        else if (!File.Exists(Path.Combine(_importPath, "server.json")))
                            UiSharedService.ColorTextWrapped("server.json not found in this folder.", ImGuiColors.DalamudYellow);
                        else
                            UiSharedService.ColorTextWrapped("✓ Valid Mare config folder detected", ImGuiColors.HealerGreen);
                    }

                    using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_importPath)))
                    {
                        if (ImGui.Button("Scan Folder"))
                        {
                            try
                            {
                                _scan = wizard.ScanFolder(_importPath);
                                if (_scan?.SourceConfig != null)
                                {
                                    _cachePathOverride = _scan.SuggestedCacheFolder ?? string.Empty;
                                    _reuseCache = _scan.FileCacheExists && !string.IsNullOrWhiteSpace(_cachePathOverride);
                                }
                            }
                            catch (Exception ex)
                            {
                                _scan = new ImportWizard.ScanResult
                                {
                                    SourceFolder = _importPath,
                                    Error = $"Scan failed: {ex.Message}"
                                };
                            }
                        }
                    }

                    ImGui.Separator();

                    if (_scan != null)
                    {
                        if (!string.IsNullOrEmpty(_scan.Error))
                        {
                            UiSharedService.ColorTextWrapped(_scan.Error, ImGuiColors.DalamudRed);
                        }
                        else
                        {
                            ImGui.TextUnformatted("Cache folder to use:");
                            ImGui.SetNextItemWidth(300);
                            ImGui.InputTextWithHint("##cachePath", "Choose a cache folder (optional)", ref _cachePathOverride);

                            ImGui.SameLine();
                            if (ImGui.Button("Browse##cache"))
                                OpenCacheFolderDialog();

                            if (_scan.FileCacheExists)
                                ImGui.Checkbox("Reuse existing FileCache.csv (skip re-scan)", ref _reuseCache);

                            ImGui.Separator();

                            using (var table = ImRaii.Table("import_servers_table", 5,
                                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                            {
                                if (table)
                                {
                                    ImGui.TableSetupColumn("Import", ImGuiTableColumnFlags.WidthFixed, 60);
                                    ImGui.TableSetupColumn("Name");
                                    ImGui.TableSetupColumn("URI");
                                    ImGui.TableSetupColumn("Auth");
                                    ImGui.TableSetupColumn("OAuth");
                                    ImGui.TableHeadersRow();

                                    for (int i = 0; i < _scan.SourceServers.Count; i++)
                                    {
                                        var row = _scan.SourceServers[i];
                                        ImGui.TableNextRow();

                                        ImGui.TableSetColumnIndex(0);
                                        var sel = row.Selected;
                                        if (ImGui.Checkbox($"##sel_{i}", ref sel))
                                            row.Selected = sel;

                                        ImGui.TableSetColumnIndex(1);
                                        if (i == _scan.SourceCurrentIndex)
                                            UiSharedService.ColorText(row.ServerName + " (current)", ImGuiColors.HealerGreen);
                                        else
                                            ImGui.TextUnformatted(row.ServerName);

                                        ImGui.TableSetColumnIndex(2);
                                        ImGui.TextUnformatted(row.ServerUri);

                                        ImGui.TableSetColumnIndex(3);
                                        ImGui.TextUnformatted(row.AuthCount.ToString());

                                        ImGui.TableSetColumnIndex(4);
                                        ImGui.TextUnformatted(row.UseOAuth2 ? (row.HasToken ? "OAuth2 ✓" : "OAuth2") : "SecretKey");
                                    }
                                }
                            }

                            ImGui.Separator();

                            var selectedCount = _scan.SourceServers.Count(s => s.Selected);
                            using (ImRaii.Disabled(selectedCount == 0))
                            {
                                if (ImGui.Button($"Import Selected ({selectedCount})"))
                                {
                                    var chosen = _scan.SourceServers.Where(s => s.Selected).ToList();
                                    var summary = wizard.ImportSelected(_scan, chosen, _cachePathOverride, _reuseCache);

                                    if (!string.IsNullOrEmpty(summary.Error))
                                    {
                                        UiSharedService.ColorTextWrapped(summary.Error, ImGuiColors.DalamudRed);
                                    }
                                    else
                                    {
                                        UiSharedService.ColorTextWrapped(
                                            $"Successfully imported {summary.Created + summary.Updated} server(s)!\n" +
                                            $"- Created: {summary.Created} new\n" +
                                            $"- Updated: {summary.Updated} existing\n" +
                                            $"- Cache: {(summary.CacheConfigured ? (_reuseCache ? "reused (skipped scan)" : "configured for new scan") : "unchanged")}",
                                            ImGuiColors.HealerGreen);

                                        // Close this window and advance
                                        _showImportModal = false;
                                        _readFirstPage = true;
#if !DEBUG
                            _timeoutTask = Task.Run(async () =>
                            {
                                for (int i = 2; i > 0; i--)
                                {
                                    _timeoutLabel = $"Continuing in {i}s";
                                    await Task.Delay(1000);
                                }
                            });
#else
                                        _timeoutTask = Task.CompletedTask;
#endif
                                    }
                                }
                            }

                            ImGui.SameLine();
                        }
                    }

                    // Close button (always)
                    if (ImGui.Button("Close"))
                        _showImportModal = false;

                    ImGui.End();
                }
            }


            /*/ Draw the file dialog AFTER the modal, so it renders on top
            if (_shouldOpenFileDialog)
            {
                _shouldOpenFileDialog = false;
                OpenFolderDialog();
            }
            */
            // Always draw the file dialog manager so it can handle callbacks
            _fileDialogManager.Draw();

            if (ImGui.Button("Next##toAgreement"))
            {
                _readFirstPage = true;
#if !DEBUG
                _timeoutTask = Task.Run(async () =>
                {
                    for (int i = 60; i > 0; i--)
                    {
                        _timeoutLabel = $"{Strings.ToS.ButtonWillBeAvailableIn} {i}s";
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                });
#else
                _timeoutTask = Task.CompletedTask;
#endif
            }
        }
        else if (!_configService.Current.AcceptedAgreement && _readFirstPage)
        {
            Vector2 textSize;
            using (_uiShared.UidFont.Push())
            {
                textSize = ImGui.CalcTextSize(Strings.ToS.LanguageLabel);
                ImGui.TextUnformatted(Strings.ToS.AgreementLabel);
            }

            ImGui.SameLine();
            var languageSize = ImGui.CalcTextSize(Strings.ToS.LanguageLabel);
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X - languageSize.X - 80);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textSize.Y / 2 - languageSize.Y / 2);

            ImGui.TextUnformatted(Strings.ToS.LanguageLabel);
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textSize.Y / 2 - (languageSize.Y + ImGui.GetStyle().FramePadding.Y) / 2);
            ImGui.SetNextItemWidth(80);
            if (ImGui.Combo("", ref _currentLanguage, _languages.Keys.ToArray(), _languages.Count))
            {
                GetToSLocalization(_currentLanguage);
            }

            ImGui.Separator();
            ImGui.SetWindowFontScale(1.5f);
            string readThis = Strings.ToS.ReadLabel;
            textSize = ImGui.CalcTextSize(readThis);
            ImGui.SetCursorPosX(ImGui.GetWindowSize().X / 2 - textSize.X / 2);
            UiSharedService.ColorText(readThis, ImGuiColors.DalamudRed);
            ImGui.SetWindowFontScale(1.0f);
            ImGui.Separator();

            UiSharedService.TextWrapped(_tosParagraphs![0]);
            UiSharedService.TextWrapped(_tosParagraphs![1]);
            UiSharedService.TextWrapped(_tosParagraphs![2]);
            UiSharedService.TextWrapped(_tosParagraphs![3]);
            UiSharedService.TextWrapped(_tosParagraphs![4]);
            UiSharedService.TextWrapped(_tosParagraphs![5]);

            ImGui.Separator();
            if (_timeoutTask?.IsCompleted ?? true)
            {
                if (ImGui.Button(Strings.ToS.AgreeLabel + "##toSetup"))
                {
                    _configService.Current.AcceptedAgreement = true;
                    _configService.Save();
                }
            }
            else
            {
                UiSharedService.TextWrapped(_timeoutLabel);
            }
        }
        else if (_configService.Current.AcceptedAgreement
                 && (string.IsNullOrEmpty(_configService.Current.CacheFolder)
                     || !_configService.Current.InitialScanComplete
                     || !Directory.Exists(_configService.Current.CacheFolder)))
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted("File Storage Setup");

            ImGui.Separator();

            if (!_uiShared.HasValidPenumbraModPath)
            {
                UiSharedService.ColorTextWrapped("You do not have a valid Penumbra path set. Open Penumbra and set up a valid path for the mod directory.", ImGuiColors.DalamudRed);
            }
            else
            {
                UiSharedService.TextWrapped("To not unnecessary download files already present on your computer, Neko-Net will have to scan your Penumbra mod directory. " +
                                     "Additionally, a local storage folder must be set where Neko-net will download other character files to. " +
                                     "Once the storage folder is set and the scan complete, this page will automatically forward to registration at a service.");
                UiSharedService.TextWrapped("Note: The initial scan, depending on the amount of mods you have, might take a while. Please wait until it is completed.");
                UiSharedService.ColorTextWrapped("Warning: once past this step you should not delete the FileCache.csv of Neko-Net in the Plugin Configurations folder of Dalamud. " +
                                          "Otherwise on the next launch a full re-scan of the file cache database will be initiated.", ImGuiColors.DalamudYellow);
                UiSharedService.ColorTextWrapped("Warning: if the scan is hanging and does nothing for a long time, chances are high your Penumbra folder is not set up properly.", ImGuiColors.DalamudYellow);
                _uiShared.DrawCacheDirectorySetting();
            }

            if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
            {
                if (ImGui.Button("Start Scan##startScan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
            else
            {
                _uiShared.DrawFileScanState();
            }
            if (!_dalamudUtilService.IsWine)
            {
                var useFileCompactor = _configService.Current.UseCompactor;
                if (ImGui.Checkbox("Use File Compactor", ref useFileCompactor))
                {
                    _configService.Current.UseCompactor = useFileCompactor;
                    _configService.Save();
                }
                UiSharedService.ColorTextWrapped("The File Compactor can save a tremendeous amount of space on the hard disk for downloads through Mare. It will incur a minor CPU penalty on download but can speed up " +
                    "loading of other characters. It is recommended to keep it enabled. You can change this setting later anytime in the Mare settings.", ImGuiColors.DalamudYellow);
            }
        }
        else if (!_uiShared.ApiController.ServerAlive)
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted("Service Registration");
            ImGui.Separator();
            UiSharedService.TextWrapped("To be able to use Neko-Net you will have to register an account.");
            UiSharedService.TextWrapped("For the official Mare Neko-Net Servers the account creation will be handled on the official  Neko-Net Discord. Due to security risks for the server, there is no way to handle this sensibly otherwise.");
            UiSharedService.TextWrapped("If you want to register at the main server \"" + ApiController.MainServer + "\" join the Discord and follow the instructions as described in #mare-service.");

            if (ImGui.Button("Join the Neko-Net Discord"))
            {
                Util.OpenLink("https://discord.gg/8yjsdMcxB4");
            }

            UiSharedService.TextWrapped("For all other non official services you will have to contact the appropriate service provider how to obtain a secret key.");

            UiSharedService.DistanceSeparator();

            UiSharedService.TextWrapped("Once you have registered you can connect to the service using the tools provided below.");

            int serverIdx = 0;
            var selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);

            using (var node = ImRaii.TreeNode("Advanced Options"))
            {
                if (node)
                {
                    serverIdx = _uiShared.DrawServiceSelection(selectOnChange: true, showConnect: false);
                    if (serverIdx != _prevIdx)
                    {
                        _uiShared.ResetOAuthTasksState();
                        _prevIdx = serverIdx;
                    }

                    selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);
                    _useLegacyLogin = !selectedServer.UseOAuth2;

                    if (ImGui.Checkbox("Use Legacy Login with Secret Key", ref _useLegacyLogin))
                    {
                        _serverConfigurationManager.GetServerByIndex(serverIdx).UseOAuth2 = !_useLegacyLogin;
                        _serverConfigurationManager.Save();
                    }
                }
            }

            if (_useLegacyLogin)
            {
                var text = "Enter Secret Key";
                var buttonText = "Save";
                var buttonWidth = _secretKey.Length != 64 ? 0 : ImGuiHelpers.GetButtonSize(buttonText).X + ImGui.GetStyle().ItemSpacing.X;
                var textSize = ImGui.CalcTextSize(text);

                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DrawGroupedCenteredColorText("Strongly consider to use OAuth2 to authenticate, if the server supports it (the current main server does). " +
                    "The authentication flow is simpler and you do not require to store or maintain Secret Keys. " +
                    "You already implicitly register using Discord, so the OAuth2 method will be cleaner and more straight-forward to use.", ImGuiColors.DalamudYellow, 500);
                ImGuiHelpers.ScaledDummy(5);

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(text);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - buttonWidth - textSize.X);
                ImGui.InputText("", ref _secretKey, 64);
                if (_secretKey.Length > 0 && _secretKey.Length != 64)
                {
                    UiSharedService.ColorTextWrapped("Your secret key must be exactly 64 characters long. Don't enter your Lodestone auth here.", ImGuiColors.DalamudRed);
                }
                else if (_secretKey.Length == 64 && !HexRegex().IsMatch(_secretKey))
                {
                    UiSharedService.ColorTextWrapped("Your secret key can only contain ABCDEF and the numbers 0-9.", ImGuiColors.DalamudRed);
                }
                else if (_secretKey.Length == 64)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(buttonText))
                    {
                        if (_serverConfigurationManager.CurrentServer == null) _serverConfigurationManager.SelectServer(0);
                        if (!_serverConfigurationManager.CurrentServer!.SecretKeys.Any())
                        {
                            _serverConfigurationManager.CurrentServer!.SecretKeys.Add(_serverConfigurationManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                            {
                                FriendlyName = $"Secret Key added on Setup ({DateTime.Now:yyyy-MM-dd})",
                                Key = _secretKey,
                            });
                            _serverConfigurationManager.AddCurrentCharacterToServer();
                        }
                        else
                        {
                            _serverConfigurationManager.CurrentServer!.SecretKeys[0] = new SecretKey()
                            {
                                FriendlyName = $"Secret Key added on Setup ({DateTime.Now:yyyy-MM-dd})",
                                Key = _secretKey,
                            };
                        }
                        _secretKey = string.Empty;
                        _ = Task.Run(() => _uiShared.SyncFacade.StartAsync(CancellationToken.None));
                    }
                }
            }
            else
            {
                if (string.IsNullOrEmpty(selectedServer.OAuthToken))
                {
                    UiSharedService.TextWrapped("Press the button below to verify the server has OAuth2 capabilities. Afterwards, authenticate using Discord in the Browser window.");
                    _uiShared.DrawOAuth(selectedServer);
                }
                else
                {
                    UiSharedService.ColorTextWrapped($"OAuth2 is connected. Linked to: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                    UiSharedService.TextWrapped("Now press the update UIDs button to get a list of all of your UIDs on the server.");
                    _uiShared.DrawUpdateOAuthUIDsButton(selectedServer);
                    var playerName = _dalamudUtilService.GetPlayerName();
                    var playerWorld = _dalamudUtilService.GetHomeWorldId();
                    UiSharedService.TextWrapped($"Once pressed, select the UID you want to use for your current character {_dalamudUtilService.GetPlayerName()}. If no UIDs are visible, make sure you are connected to the correct Discord account. " +
                        $"If that is not the case, use the unlink button below (hold CTRL to unlink).");
                    _uiShared.DrawUnlinkOAuthButton(selectedServer);

                    var auth = selectedServer.Authentications.Find(a => string.Equals(a.CharacterName, playerName, StringComparison.Ordinal) && a.WorldId == playerWorld);
                    if (auth == null)
                    {
                        auth = new Authentication()
                        {
                            CharacterName = playerName,
                            WorldId = playerWorld
                        };
                        selectedServer.Authentications.Add(auth);
                        _serverConfigurationManager.Save();
                    }

                    _uiShared.DrawUIDComboForAuthentication(0, auth, selectedServer.ServerUri);

                    using (ImRaii.Disabled(string.IsNullOrEmpty(auth.UID)))
                    {
                        if (_uiShared.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Link, "Connect to Service"))
                        {
                            _ = Task.Run(() => _uiShared.SyncFacade.StartAsync(CancellationToken.None));
                        }
                    }
                    if (string.IsNullOrEmpty(auth.UID))
                        UiSharedService.AttachToolTip("Select a UID to be able to connect to the service");
                }
            }
        }
        else
        {
            Mediator.Publish(new SwitchToMainUiMessage());
            IsOpen = false;
        }
    }

    private void GetToSLocalization(int changeLanguageTo = -1)
    {
        if (changeLanguageTo != -1)
        {
            _uiShared.LoadLocalization(_languages.ElementAt(changeLanguageTo).Value);
        }

        _tosParagraphs = [Strings.ToS.Paragraph1, Strings.ToS.Paragraph2, Strings.ToS.Paragraph3, Strings.ToS.Paragraph4, Strings.ToS.Paragraph5, Strings.ToS.Paragraph6];
    }

    [GeneratedRegex("^([A-F0-9]{2})+")]
    private static partial Regex HexRegex();
}
