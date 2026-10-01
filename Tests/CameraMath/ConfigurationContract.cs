// Test-only host contract: tests run without loading Dalamud or the game.
namespace Dalamud.Configuration;
public interface IPluginConfiguration { int Version { get; set; } }
