// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DynamicPortrait.Camera;
using DynamicPortrait.Rendering;
using DynamicPortrait.UI;
using System.Text.Json;

namespace DynamicPortrait;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IClientState client;
    private readonly Configuration config;
    private readonly SubjectResolver subjects;
    private readonly PortraitRenderer renderer;
    private readonly PortraitUi windows;
    private RenderInvestigation? investigation;
    private bool disposed;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IClientState client,
        IObjectTable objects, ITargetManager targets, IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log, ICondition conditions, IFramework framework)
    {
        this.pi = pi;
        this.commands = commands;
        this.client = client;
        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Normalize();
        subjects = new SubjectResolver(objects, targets);
        renderer = new PortraitRenderer(config, subjects, interop, scanner, log, client, conditions, framework);
        windows = new PortraitUi(config, subjects, renderer, Save);
        commands.AddHandler("/dportrait", new CommandInfo(OnCommand) { HelpMessage = "Dynamic Portrait: settings | on | off | toggle | reset (window) | resetall" });
        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenConfigUi += windows.OpenSettings;
        pi.UiBuilder.OpenMainUi += windows.OpenSettings;
        client.TerritoryChanged += OnTerritory;
        var requestPath = Path.Combine(pi.ConfigDirectory.FullName, "render-investigation.request.json");
        if (File.Exists(requestPath))
        {
            try
            {
                var request = JsonSerializer.Deserialize<RenderInvestigation.Request>(File.ReadAllText(requestPath));
                File.Delete(requestPath);
                if (request != null && request.ExpiresUtc.ToUniversalTime() > DateTime.UtcNow)
                    _ = framework.RunOnTick(() =>
                    {
                        if (disposed) return;
                        try { investigation = new(renderer, framework, interop, scanner, log,
                            Path.Combine(pi.ConfigDirectory.FullName, "diagnostics"), request.SecondsPerPhase); }
                        catch (Exception e) { log.Error(e, "Could not start render investigation"); }
                    }, delayTicks: 10);
            }
            catch (Exception e) { log.Error(e, "Could not consume render investigation request"); }
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": config.ShowPortrait = true; renderer.SetEnabled(true); break;
            case "off": renderer.SetEnabled(false); break;
            case "toggle": config.ShowPortrait = !config.ShowPortrait; break;
            case "reset": windows.ResetWindow(); break;
            case "resetall": windows.ResetAll(); break;
            default: windows.OpenSettings(); break;
        }
        Save();
    }

    private void OnTerritory(uint _)
    {
        subjects.Clear();
        renderer.InvalidateSubject();
    }
    private void Save() { config.Normalize(); pi.SavePluginConfig(config); }

    public void Dispose()
    {
        disposed = true;
        pi.UiBuilder.Draw -= windows.Draw;
        pi.UiBuilder.OpenConfigUi -= windows.OpenSettings;
        pi.UiBuilder.OpenMainUi -= windows.OpenSettings;
        client.TerritoryChanged -= OnTerritory;
        commands.RemoveHandler("/dportrait");
        investigation?.Dispose();
        renderer.Dispose();
        Save();
    }
}
