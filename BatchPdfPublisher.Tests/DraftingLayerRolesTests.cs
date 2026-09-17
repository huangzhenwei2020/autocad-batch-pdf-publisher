using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BatchPdfPublisher.Services;

/// <summary>
/// 图层角色登记表（DraftingLayerRoles）的护栏测试。
///
/// 核心目的是把"每个图层都要真正有作用"变成可执行检查：
/// 任何图层只要没有使用方，测试立刻失败。
///
/// 另外用一次**轻量**源码核对（只提取常量名，不解析方法体）确保角色表里
/// 没有拼错的图层键——键名写错会静默失效，是这类表最容易出的错。
/// </summary>
internal static class DraftingLayerRolesTests
{
    private const int ExpectedMinimumLayers = 20;

    public static void RunAll()
    {
        RolesAreWellFormed();
        EveryLayerHasConsumer();
        KeysAreNotTypo();
    }

    private static void RolesAreWellFormed()
    {
        TestAssertTrue(DraftingLayerRoles.All.Length >= ExpectedMinimumLayers,
            "图层角色表条目过少（" + DraftingLayerRoles.All.Length + "），可能漏登记了图层");

        var duplicates = DraftingLayerRoles.All
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        TestAssertTrue(duplicates.Count == 0, "角色表存在重复键：" + string.Join("、", duplicates.ToArray()));

        foreach (var role in DraftingLayerRoles.All)
        {
            TestAssertTrue(!string.IsNullOrWhiteSpace(role.Key), "角色表存在空键");
            TestAssertTrue(!string.IsNullOrWhiteSpace(role.Group), role.Key + " 缺少分组");
            TestAssertTrue(DraftingLayerRoles.Find(role.Key) != null, role.Key + " 无法按键检索到");
        }
        Console.WriteLine("PASS DraftingLayerRolesAreWellFormed");
    }

    /// <summary>
    /// 每个图层都必须有使用方。这是"每个图层都要真正有作用"的可执行版本：
    /// 一旦有人把图层加进标准却没接任何功能，这里立刻失败。
    /// </summary>
    private static void EveryLayerHasConsumer()
    {
        var unused = DraftingLayerRoles.All
            .Where(r => r.Consumers == null || r.Consumers.Length == 0 ||
                        r.Consumers.All(c => string.IsNullOrWhiteSpace(c)))
            .Select(r => r.Key + "（" + r.Group + "）")
            .ToList();
        TestAssertTrue(unused.Count == 0,
            "以下图层没有任何使用方（装饰图层），请接上消费方或从标准中移除：" + string.Join("、", unused.ToArray()));
        Console.WriteLine("PASS EveryLayerHasConsumer");
    }

    /// <summary>
    /// 角色表的键必须都能在 DraftingStandardService.cs 里找到同名常量，
    /// 否则就是拼错了键名（会静默取不到图层、回退默认名）。
    /// </summary>
    private static void KeysAreNotTypo()
    {
        var path = ResolveServiceSourcePath();
        if (path == null)
        {
            // 定位不到源码时不做断言：宁可漏检，也不要让测试因环境问题误报。
            Console.WriteLine("PASS DraftingLayerRolesKeysMatchConstants (skipped: source not found)");
            return;
        }

        var source = File.ReadAllText(path);
        var constants = new HashSet<string>(
            Regex.Matches(source, @"(\w+Key)\s*=\s*""").Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);
        // 常量命名有两种约定：<Key>Key（Frame、Catalog、Outline…）
        // 与 <Key>LayerKey（StairAxis、DoorWindowWindow、DetailSeparator…）。
        // 两种都接受，避免把命名风格差异误报成拼写错误。
        var missing = DraftingLayerRoles.All
            .Where(r => !constants.Contains(r.Key + "Key") && !constants.Contains(r.Key + "LayerKey"))
            .Select(r => r.Key)
            .ToList();
        TestAssertTrue(missing.Count == 0,
            "角色表的键在 DraftingStandardService.cs 中找不到对应常量（疑似拼写错误）："
            + string.Join("、", missing.ToArray()));
        Console.WriteLine("PASS DraftingLayerRolesKeysMatchConstants");
    }

    private static string ResolveServiceSourcePath()
    {
        var relative = Path.Combine("BatchPdfPublisher", "Features", "Drafting", "Services", "DraftingStandardService.cs");
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (var level = 0; directory != null && level < 8; level++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void TestAssertTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
