using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFAAvalonia.Extensions.MaaFW.Custom;

/// <summary>
/// 「发起组队」节点使用的自定义动作：跨配置检查当前 MFA 其它配置已保存的任务列表，
/// 把「勾选了 队伍命令 任务、且 队伍命令 的选项解析为『本队队员』」的配置逐个执行「开始任务」。
///
/// 本配置自己的队伍身份/编号从本配置的「队伍命令」任务选项读取（缺失时回退「发起组队」的选项，
/// 再回退 custom_action_param 的 team），因为未启动过的配置还没有 agent 进程，只能由主进程拉起。
///
/// custom_action_param（全部可省略）：
/// {
///     "task_name": "队伍命令",       // 目标任务名
///     "role_option": "队伍角色",     // 身份选项名
///     "team_option": "队伍编号",     // 队伍编号选项名
///     "member_role": "队员",         // 队员 case 名
///     "team": "",                    // 可选：显式指定本队 case 名（覆盖读配置）
///     "trigger_task_name": "发起组队", // 本配置身份读取的回退任务名
///     "skip_running": true           // 目标已在运行则跳过
/// }
/// </summary>
public class StartTeamMembersAction : IMaaCustomAction
{
    private const string DefaultTaskName = "队伍命令";
    private const string DefaultRoleOption = "队伍角色";
    private const string DefaultTeamOption = "队伍编号";
    private const string DefaultMemberRole = "队员";
    private const string DefaultTriggerTaskName = "发起组队";

    /// <summary>当前 action 所属实例（运行本节点的那个配置）。</summary>
    private readonly MaaProcessor _owner;

    public string Name { get; set; } = nameof(StartTeamMembersAction);

    public StartTeamMembersAction(MaaProcessor owner) => _owner = owner;

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        try
        {
            var param = ActionParamHelper.Parse(args.ActionParam);

            var taskName = ReadString(param, "task_name", DefaultTaskName);
            var roleOption = ReadString(param, "role_option", DefaultRoleOption);
            var teamOption = ReadString(param, "team_option", DefaultTeamOption);
            var memberRole = ReadString(param, "member_role", DefaultMemberRole);
            var triggerTaskName = ReadString(param, "trigger_task_name", DefaultTriggerTaskName);
            var teamOverride = ReadString(param, "team", string.Empty);
            var skipRunning = (bool?)param["skip_running"] ?? true;

            // 1. 本配置的队伍编号 / 身份
            var selfEntries = ReadOwnerEntries();
            var identity = ResolveIdentity(selfEntries, taskName, triggerTaskName, roleOption, teamOption);
            var team = !string.IsNullOrWhiteSpace(teamOverride)
                ? teamOverride.Trim()
                : identity?.Team;
            var role = identity?.Role;

            if (string.IsNullOrWhiteSpace(team))
            {
                LoggerHelper.Warning(
                    $"[StartTeamMembersAction] 读不到本配置的队伍编号（{taskName} / {triggerTaskName} 的 {teamOption} 选项都缺失），本次不拉起任何配置");
                _owner.AddLog($"[发起组队] 读不到本配置的队伍编号（{teamOption}），未拉起任何配置");
                return true;
            }

            LoggerHelper.Info(
                $"[StartTeamMembersAction] 本配置 {_owner.InstanceId} 身份={role ?? "未识别"}，队伍={team}，开始检查其它配置");

            // 2. 扫描其它配置
            var matched = new List<string>();
            foreach (var instanceId in EnumerateOtherInstanceIds())
            {
                var targetEntries = ReadEntriesFromFile(instanceId);
                var entry = FindTask(targetEntries, taskName);
                if (entry == null)
                {
                    continue;
                }

                if (!entry.Checked)
                {
                    LoggerHelper.Debug($"[StartTeamMembersAction] 配置 {instanceId} 未勾选 {taskName}，跳过");
                    continue;
                }

                // 与队长侧同一套规则：见 PickCase（勾选的任务 > 显式保存过的索引 > 非默认项 > 候选顺序）
                var candidates = CandidateEntries(targetEntries, taskName, triggerTaskName);
                var memberRoleValue = PickCase(candidates, roleOption);
                var memberTeamValue = PickCase(candidates, teamOption);
                LoggerHelper.Info(
                    $"[StartTeamMembersAction] 配置 {instanceId}：{roleOption}={memberRoleValue ?? "未识别"}，{teamOption}={memberTeamValue ?? "未识别"}");

                if (!string.Equals(memberRoleValue, memberRole, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.Equals(memberTeamValue, team, StringComparison.Ordinal))
                {
                    continue;
                }

                matched.Add(instanceId);
            }

            if (matched.Count == 0)
            {
                LoggerHelper.Warning(
                    $"[StartTeamMembersAction] 没有找到「勾选 {taskName} 且 {roleOption}={memberRole}、{teamOption}={team}」的其它配置");
                _owner.AddLog($"[发起组队] 没有匹配的队伍命令配置（{memberRole} / {team}），未拉起任何配置");
                return true;
            }

            // 3. 逐个执行「开始任务」（不阻塞任务回调线程，不抢当前标签页）
            var names = new List<string>();
            foreach (var instanceId in matched)
            {
                names.Add($"{instanceId}({MaaProcessorManager.Instance.GetInstanceName(instanceId)})");
                var target = instanceId;
                _ = Task.Run(() => StartInstanceAsync(target, skipRunning));
            }

            LoggerHelper.Info($"[StartTeamMembersAction] 准备拉起 {matched.Count} 个配置：{string.Join("、", names)}");
            _owner.AddLog($"[发起组队] 已为 {matched.Count} 个队员配置执行开始任务：{string.Join("、", names)}");
            return true;
        }
        catch (Exception e)
        {
            // 尽力而为：拉起队员失败不应阻断队长的组队流程
            LoggerHelper.Error($"[StartTeamMembersAction] Error: {e.Message}", e);
            try
            {
                _owner.AddLog($"[发起组队] 拉起队员配置失败：{e.Message}");
            }
            catch
            {
                // ignored
            }

            return true;
        }
    }

    // ------------------------------------------------------------------
    // 启动目标配置
    // ------------------------------------------------------------------

    /// <summary>
    /// 拉起一个目标配置。整段（懒加载配置、取界面模型、恢复连接目标、开始任务）都放到主线程，
    /// 与「全局启动」（GlobalStartManager.StartInstance）保持同一套线程模型：
    /// 界面模型是 Avalonia 对象，在主线程之外创建/驱动容易出隐性故障。
    /// 由 Task.Run 调用，所以不会卡住任务回调线程。
    /// </summary>
    private async Task StartInstanceAsync(string instanceId, bool skipRunning)
    {
        var started = false;
        try
        {
            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                var manager = MaaProcessorManager.Instance;
                manager.EnsureInstanceLoaded(instanceId);

                if (!manager.TryGetInstance(instanceId, out var processor) || processor == null)
                {
                    LoggerHelper.Warning($"[StartTeamMembersAction] 目标配置加载失败：{instanceId}");
                    _owner.AddLog($"[发起组队] 目标配置加载失败：{instanceId}");
                    return;
                }

                if (skipRunning && processor.IsTaskRunActive)
                {
                    LoggerHelper.Info($"[StartTeamMembersAction] 目标配置 {instanceId} 已在运行，跳过");
                    _owner.AddLog($"[发起组队] 配置 {instanceId} 已在运行，跳过");
                    return;
                }

                var vm = manager.GetViewModel(instanceId);
                if (vm == null)
                {
                    LoggerHelper.Warning($"[StartTeamMembersAction] 目标配置没有可用的界面模型：{instanceId}");
                    _owner.AddLog($"[发起组队] 目标配置不可用：{instanceId}");
                    return;
                }

                // 与“全局启动”一致：先按本配置记住的窗口恢复连接目标，再走开始任务入口
                vm.TryReadAdbDeviceFromConfig(inTask: false, refresh: false, allowAutoDetect: true, showToast: false);
                vm.StartTask();
                started = true;
            });

            if (started)
            {
                LoggerHelper.Info($"[StartTeamMembersAction] 已为配置 {instanceId} 执行开始任务");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[StartTeamMembersAction] 为配置 {instanceId} 执行开始任务失败：{ex.Message}", ex);
            _owner.AddLog($"[发起组队] 配置 {instanceId} 启动失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 配置读取
    // ------------------------------------------------------------------

    private IEnumerable<string> EnumerateOtherInstanceIds()
    {
        var ids = new List<string>();

        try
        {
            foreach (var (id, _) in MaaProcessorManager.Instance.GetAllInstanceIdsAndNames())
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[StartTeamMembersAction] 读取已注册配置列表失败：{ex.Message}");
        }

        try
        {
            if (Directory.Exists(AppPaths.InstancesDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.InstancesDirectory, "*.json"))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[StartTeamMembersAction] 扫描配置目录失败：{ex.Message}");
        }

        return ids
            .Where(id => !string.Equals(id, _owner.InstanceId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<TaskEntry> ReadOwnerEntries()
    {
        var entries = ReadEntriesFromFile(_owner.InstanceId);
        if (entries.Count > 0)
        {
            return entries;
        }

        // 兜底：内存里已加载的配置
        try
        {
            var items = _owner.InstanceConfiguration
                .GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>());
            return (items ?? new List<MaaInterface.MaaInterfaceTask>())
                .Select(ToTaskEntry)
                .Where(entry => entry != null)
                .Select(entry => entry!)
                .ToList();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[StartTeamMembersAction] 读取本配置任务列表失败：{ex.Message}");
            return entries;
        }
    }

    /// <summary>
    /// 只读读取指定配置的 config/instances/{id}.json；不做任何写盘/迁移。
    /// </summary>
    private static List<TaskEntry> ReadEntriesFromFile(string instanceId)
    {
        try
        {
            var path = Path.Combine(AppPaths.InstancesDirectory, $"{instanceId}.json");
            if (!File.Exists(path))
            {
                return new List<TaskEntry>();
            }

            var root = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (root is not JObject obj || obj[ConfigurationKeys.TaskItems] is not JArray items)
            {
                LoggerHelper.Warning($"[StartTeamMembersAction] 配置 {instanceId} 没有可用的 {ConfigurationKeys.TaskItems}");
                return new List<TaskEntry>();
            }

            return items
                .Select(ToTaskEntry)
                .Where(entry => entry != null)
                .Select(entry => entry!)
                .ToList();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[StartTeamMembersAction] 读取配置 {instanceId} 失败：{ex.Message}");
            return new List<TaskEntry>();
        }
    }

    private static TaskEntry? FindTask(IEnumerable<TaskEntry> entries, string taskName)
        => entries.FirstOrDefault(entry => string.Equals(entry.Name, taskName, StringComparison.Ordinal));

    private static TaskEntry? ToTaskEntry(MaaInterface.MaaInterfaceTask? task)
    {
        if (task == null || string.IsNullOrWhiteSpace(task.Name))
        {
            return null;
        }

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var option in task.Option ?? new List<MaaInterface.MaaInterfaceSelectOption>())
        {
            if (string.IsNullOrWhiteSpace(option.Name) || option.Index is not int index)
            {
                continue;
            }

            indexes[option.Name!] = index;
        }

        return new TaskEntry(task.Name!, task.Check == true, indexes);
    }

    private static TaskEntry? ToTaskEntry(JToken token)
    {
        if (token is not JObject obj)
        {
            return null;
        }

        var name = (string?)obj["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (obj["option"] is JArray options)
        {
            foreach (var option in options)
            {
                var optionName = (string?)option["name"];
                if (string.IsNullOrWhiteSpace(optionName))
                {
                    continue;
                }

                var rawIndex = option["index"];
                if (rawIndex == null || rawIndex.Type == JTokenType.Null)
                {
                    continue;
                }

                if (int.TryParse(rawIndex.ToString(), out var index))
                {
                    indexes[optionName!] = index;
                }
            }
        }

        // 与 GUI 一致：default_check 缺失/非 true 视为未勾选
        var checkToken = obj["default_check"];
        var isChecked = checkToken?.Type == JTokenType.Boolean && checkToken.Value<bool>();

        return new TaskEntry(name!, isChecked, indexes);
    }

    // ------------------------------------------------------------------
    // 选项解析
    // ------------------------------------------------------------------

    private static (string Role, string Team)? ResolveIdentity(
        IEnumerable<TaskEntry> entries,
        string taskName,
        string fallbackTaskName,
        string roleOption,
        string teamOption)
    {
        var candidates = CandidateEntries(entries, taskName, fallbackTaskName);
        if (candidates.Count == 0)
        {
            return null;
        }

        var role = PickCase(candidates, roleOption);
        var team = PickCase(candidates, teamOption);
        if (string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(team))
        {
            return null;
        }

        return (role ?? string.Empty, team ?? string.Empty);
    }

    /// <summary>
    /// 身份读取用的候选任务：目标任务的选项优先，回退任务的选项次之。
    /// </summary>
    private static List<TaskEntry> CandidateEntries(
        IEnumerable<TaskEntry> entries,
        string taskName,
        string fallbackTaskName)
    {
        var candidates = new List<TaskEntry>();
        var primary = FindTask(entries, taskName);
        if (primary != null)
        {
            candidates.Add(primary);
        }

        var fallback = FindTask(entries, fallbackTaskName);
        if (fallback != null && !ReferenceEquals(fallback, primary))
        {
            candidates.Add(fallback);
        }

        return candidates;
    }

    /// <summary>
    /// 取某个选项的 case 名。候选是「队伍命令」→「发起组队」两份选项（见 CandidateEntries），
    /// 按三个信号打分，取分最高的那个（同分时保留先出现的候选，也就是「队伍命令」优先）：
    ///
    ///   4 分：该任务在本配置里是**勾选**的 —— 这才是这个配置真正会跑的任务，它的选项才代表行为；
    ///   2 分：该任务**显式保存过**这个选项的索引（而不是"没保存、回落到默认项"）；
    ///   1 分：该值不是默认项（默认项经常只是"没填过"的痕迹）。
    ///
    /// 不能只看"是不是默认项"（旧规则）：队员配置的「队伍命令」正常就是默认项 队员，
    /// 而**没勾选**的「发起组队」里可能躺着一个陈旧的 队长 索引（早期版本没有 default_case 时
    /// 写进去的 0），旧规则会把它当成"用户特意填的"优先采用 → 队员全被解析成队长 →
    /// 队长一个队员都拉不起来（2026-10-30 实测：4 个队员配置全部解析成 队伍角色=队长）。
    /// </summary>
    private static string? PickCase(IReadOnlyList<TaskEntry> candidates, string optionName)
    {
        string? best = null;
        var bestScore = int.MinValue;
        foreach (var entry in candidates)
        {
            var name = ResolveCaseName(entry, optionName, out var isDefaultCase);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var explicitIndex = entry.OptionIndexes.ContainsKey(optionName);
            var score = (entry.Checked ? 4 : 0)
                + (explicitIndex ? 2 : 0)
                + (isDefaultCase ? 0 : 1);

            LoggerHelper.Debug(
                $"[StartTeamMembersAction] 选项 {optionName} 候选：{entry.Name}（勾选={entry.Checked}、显式索引={explicitIndex}）→ {name}，{score} 分");

            if (score > bestScore)
            {
                bestScore = score;
                best = name;
            }
        }

        return best;
    }

    /// <summary>
    /// 把配置里的 option index 解析成 interface.json 的 case 名；
    /// index 缺省时与 TaskLoader.SetDefaultOptionValue 一致：default_case → 第 0 个 case。
    /// isDefaultCase = 该值就是默认项（没保存过索引，或索引恰好等于默认项）。
    /// </summary>
    private static string? ResolveCaseName(TaskEntry entry, string optionName, out bool isDefaultCase)
    {
        isDefaultCase = false;
        var interfaceOption = MaaProcessor.Interface?.Option?.TryGetValue(optionName, out var option) == true
            ? option
            : null;
        if (interfaceOption?.Cases is not { Count: > 0 } cases)
        {
            LoggerHelper.Warning($"[StartTeamMembersAction] interface.json 里找不到选项定义：{optionName}");
            return null;
        }

        var defaultIndex = interfaceOption.DefaultCase == null
            ? 0
            : cases.FindIndex(item => item.Name == interfaceOption.DefaultCase);
        if (defaultIndex < 0)
        {
            defaultIndex = 0;
        }

        var index = entry.OptionIndexes.TryGetValue(optionName, out var savedIndex) ? savedIndex : -1;
        if (index < 0)
        {
            index = defaultIndex;
            isDefaultCase = true;
        }
        else if (index == defaultIndex)
        {
            isDefaultCase = true;
        }

        return index >= 0 && index < cases.Count ? cases[index].Name : null;
    }

    private static string ReadString(JObject param, string key, string fallback)
    {
        var value = (string?)param[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record TaskEntry(string Name, bool Checked, Dictionary<string, int> OptionIndexes);
}
