using Microsoft.Extensions.Options;
using NxtUI.Core.Configuration;
using NxtUI.Core.Services;

namespace NxtUI.Web.Services;

public sealed class EnvConfigService(
    IOptions<EnvConfigSettings> options,
    IEnvironmentService environmentSvc,
    ILogger<EnvConfigService> log) : IEnvConfigService
{
    private EnvConfigSettings S => options.Value;

    public bool IsEditingEnabled(string env) => S.EditingEnabled && GetSourcePath(env) is not null;

    public string? GetSourcePath(string env)
    {
        if (string.IsNullOrWhiteSpace(S.SourcePathTemplate)) return null;
        var server = environmentSvc.GetServers(env).FirstOrDefault();
        if (server is null) return null;
        return Expand(S.SourcePathTemplate, server, env);
    }

    public IReadOnlyList<string> GetSaveTargets(string env)
    {
        var source = GetSourcePath(env);
        if (source is null) return [];

        var targets = new List<string> { source };
        if (S.SaveMode == EnvConfigSaveMode.AllServers && !string.IsNullOrWhiteSpace(S.PathTemplate))
        {
            foreach (var server in environmentSvc.GetServers(env))
            {
                var path = Expand(S.PathTemplate, server, env);
                if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase))
                    targets.Add(path);
            }
        }
        return targets;
    }

    public async Task<IReadOnlyList<EnvConfigSaveResult>> SaveAsync(string env, string content, CancellationToken ct = default)
    {
        var source = GetSourcePath(env);
        if (source is null)
            return [new EnvConfigSaveResult("(unresolved)", false, "Could not resolve the source config path for this environment.")];

        var results = new List<EnvConfigSaveResult>();

        try
        {
            if (File.Exists(source))
            {
                var backupPath = $"{source}.{DateTime.Now.ToString(BackupTimestampFormat)}.bak";
                await Task.Run(() => File.Copy(source, backupPath, overwrite: false), ct);
                log.LogInformation("envconfig [{Env}]: backed up {Source} -> {Backup}", env, source, backupPath);
            }
            await File.WriteAllTextAsync(source, content, ct);
            results.Add(new EnvConfigSaveResult(source, true, null));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "envconfig [{Env}]: failed to save source {Source}", env, source);
            results.Add(new EnvConfigSaveResult(source, false, ex.Message));
            return results; // source of truth failed — don't propagate a broken save to mirrors
        }

        if (S.SaveMode == EnvConfigSaveMode.AllServers)
        {
            foreach (var target in GetSaveTargets(env).Where(p => !string.Equals(p, source, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await File.WriteAllTextAsync(target, content, ct);
                    results.Add(new EnvConfigSaveResult(target, true, null));
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "envconfig [{Env}]: failed to mirror to {Target}", env, target);
                    results.Add(new EnvConfigSaveResult(target, false, ex.Message));
                }
            }
        }

        return results;
    }

    private const string BackupTimestampFormat = "yyyyMMdd_HHmmss";

    public IReadOnlyList<EnvConfigBackup> ListBackups(string env)
    {
        var source = GetSourcePath(env);
        if (source is null) return [];

        var dir = Path.GetDirectoryName(source);
        var fileName = Path.GetFileName(source);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];

        var backups = new List<EnvConfigBackup>();
        foreach (var path in Directory.EnumerateFiles(dir, $"{fileName}.*.bak"))
        {
            var stamp = Path.GetFileName(path)[(fileName.Length + 1)..^".bak".Length];
            if (DateTime.TryParseExact(stamp, BackupTimestampFormat, null,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var takenLocal))
            {
                backups.Add(new EnvConfigBackup(path, takenLocal.ToUniversalTime()));
            }
        }
        return backups.OrderByDescending(b => b.TakenAtUtc).ToList();
    }

    private static string Expand(string template, string server, string env) =>
        template.Replace("{server}", server, StringComparison.OrdinalIgnoreCase)
                 .Replace("{env}", env, StringComparison.OrdinalIgnoreCase);
}
