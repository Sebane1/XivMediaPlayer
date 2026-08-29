using System;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer
{
    public class CommandManager : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly DependencyUpdateManager _depUpdateManager;
        private readonly ICommandManager _commandManager;
        private readonly string _commandName = "/xmpupdate";

        public CommandManager(ICommandManager commandManager, IPluginLog pluginLog, 
            DependencyUpdateManager depUpdateManager)
        {
            _pluginLog = pluginLog;
            _depUpdateManager = depUpdateManager;
            _commandManager = commandManager;

            // Register the update command
            _commandManager.AddHandler(_commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Manually check and update dependencies for XivMediaPlayer"
            });
        }

        private void OnCommand(string command, string arguments)
        {
            try
            {
                Task.Run(async () => 
                {
                    _pluginLog.Information("Manual dependency update triggered by user");
                    
                    if (await _depUpdateManager.CheckAndUpdateDependenciesAsync())
                    {
                        _pluginLog.Information("Dependency update completed successfully");
                    }
                    else
                    {
                        _pluginLog.Information("All dependencies are already up to date");
                    }
                }).Wait(30000); // Wait up to 30 seconds for updates
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error during manual update: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _commandManager.RemoveHandler(_commandName);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error disposing command manager: {ex.Message}");
            }
        }
    }
}