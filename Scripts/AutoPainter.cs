using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using Godot;

using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace Test.Scripts;

[HarmonyPatch(typeof(NClickableControl), "OnRelease")]
public static class Patch_NClickableControl_OnRelease
{
    // Postfix, 也就是原方法执行后执行
    static void Postfix(NClickableControl __instance)
    {
        // 只有当点击的是图例项时才进行处理
        if (__instance is NMapLegendItem legendItem)
        {
            var cb = legendItem.GetNodeOrNull<CheckBox>("AutoPainterCheckBox");
            if (cb != null)
            {
                cb.ButtonPressed = !cb.ButtonPressed; // 触发 Toggled 信号
            }
        }
    }
}

[HarmonyPatch(typeof(NMapLegendItem), "_Ready")]
public static class Patch_NMapLegendItem_Ready
{
    static void Postfix(NMapLegendItem __instance)
    {
        var cb = new CheckBox();
        cb.Name = "AutoPainterCheckBox";
        // 将复选框放在图例项右侧
        cb.Position = new Vector2(240, 0); 
        cb.Scale = new Vector2(1.0f, 1.0f);
        __instance.AddChild(cb);

        var pointType = Traverse.Create(__instance).Field("_pointType").GetValue<MapPointType>();
        cb.Toggled += (isSelected) => AutoPainter.ToggleType(pointType, isSelected);
    }
}

[HarmonyPatch(typeof(NMapScreen), "_Ready")]
public static class Patch_NMapScreen_Ready
{
    static void Postfix(NMapScreen __instance)
    {
        var legend = Traverse.Create(__instance).Field("_mapLegend").GetValue<Control>();
        if (legend == null) return;

        var btn = new Button();
        btn.Name = "NextRouteButton";
        btn.Text = "Next Route";
        // 放在图例区域的右下角
        btn.Position = new Vector2(50, 480); 
        btn.Size = new Vector2(120, 40);
        legend.AddChild(btn);
        
        btn.Pressed += () => AutoPainter.CyclePath();
    }
}

public static class AutoPainter
{
    internal static readonly Logger Log = new Logger("AutoPainter", LogType.Generic);

    private static HashSet<MapPointType> _selectedTypes = new();
    private static MapPoint? _lastStartPoint = null;
    private static List<List<MapPoint>> _cachedPaths = new();
    private static int _pathCycleIndex = 0;
    private static HashSet<MapPointType> _lastCalculationSet = new();

    public static void CyclePath()
    {
        if (_cachedPaths.Count > 1)
        {
            _pathCycleIndex = (_pathCycleIndex + 1) % _cachedPaths.Count;
            Log.Debug($"Cycling to path index {_pathCycleIndex} of {_cachedPaths.Count}");
            PaintRoute();
        }
    }

    public static void ToggleType(MapPointType type, bool isSelected)
    {
        if (isSelected)
        {
            _selectedTypes.Add(type);
        }
        else
        {
            _selectedTypes.Remove(type);
        }
        PaintRoute();
    }

    public static void PaintRoute()
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null)
        {
            Log.Warn("NMapScreen.Instance is null");
            return;
        }

        var drawings = mapScreen.Drawings;
        if (drawings == null)
        {
            Log.Warn("Drawings is null");
            return;
        }

        // 获取私有字段
        var traverse = Traverse.Create(mapScreen);
        var runState = traverse.Field("_runState").GetValue();
        var map = traverse.Field("_map").GetValue<ActMap>();
        var pointNodes = traverse.Field("_mapPointDictionary").GetValue<Dictionary<MapCoord, NMapPoint>>();

        if (runState == null || map == null || pointNodes == null)
        {
            Log.Warn("Map data is missing");
            return;
        }

        // 确定开始点
        MapPoint? startPoint = null;
        var visited = Traverse.Create(runState).Field("VisitedMapCoords").GetValue() as IEnumerable<MapCoord>;
        
        if (visited != null && visited.Any())
        {
            var lastCoord = visited.Last();
            startPoint = map.GetPoint(lastCoord);
        }
        else
        {
            startPoint = map.StartingMapPoint;
        }

        if (startPoint == null)
        {
            Log.Warn("Could not find start point");
            return;
        }

        // 检查是否需要重新计算或切换路径
        bool selectionChanged = !_selectedTypes.SetEquals(_lastCalculationSet);
        if (selectionChanged || startPoint != _lastStartPoint)
        {
            _lastCalculationSet = new HashSet<MapPointType>(_selectedTypes);
            _lastStartPoint = startPoint;
            
            if (_selectedTypes.Count == 0)
            {
                _cachedPaths = new List<List<MapPoint>>();
            }
            else
            {
                _cachedPaths = FindAllBestPathsForSet(startPoint, _selectedTypes);
            }
            
            _pathCycleIndex = 0;
            Log.Debug($"Calculated {_cachedPaths.Count} best paths for selected set");
        }
        else if (_cachedPaths.Count > 0)
        {
            _pathCycleIndex = (_pathCycleIndex + 1) % _cachedPaths.Count;
            Log.Debug($"Cycling to path index {_pathCycleIndex} of {_cachedPaths.Count}");
        }

        if (_cachedPaths.Count == 0 || _selectedTypes.Count == 0)
        {
            Log.Debug("No route to draw (no selection or no path)");
            drawings.ClearDrawnLinesLocal();
            return;
        }

        var path = _cachedPaths[_pathCycleIndex];

        // 清除现有的本地绘图
        drawings.ClearDrawnLinesLocal();

        // 自动绘画
        
        Log.Debug($"Painting route for {string.Join(", ", _selectedTypes)} with {path.Count} points");

        bool started = false;
        foreach (var point in path)
        {
            if (pointNodes.TryGetValue(point.coord, out var node))
            {
                // 获取节点的全局中心点
                // NMapPoint 继承自 Control，拥有 Size 和 GlobalPosition 属性
                Vector2 globalCenter = node.GlobalPosition + node.Size * 0.5f;
                // 转换回 drawings 的本地坐标空间
                Vector2 pos = globalCenter - drawings.GlobalPosition;

                if (!started)
                {
                    drawings.BeginLineLocal(pos, DrawingMode.Drawing);
                    started = true;
                }
                else
                {
                    drawings.UpdateCurrentLinePositionLocal(pos);
                }
            }
        }
        
        if (started)
        {
            drawings.StopLineLocal();
        }
    }

    private static List<List<MapPoint>> FindAllBestPathsForSet(MapPoint start, HashSet<MapPointType> selectedTypes)
    {
        var memo = new Dictionary<MapPoint, (int score, List<MapPoint> nexts)>();

        (int score, List<MapPoint> nexts) Calculate(MapPoint node)
        {
            if (memo.TryGetValue(node, out var res)) return res;

            int nodeScore = selectedTypes.Contains(node.PointType) ? 1 : 0;
            
            if (node.Children == null || node.Children.Count == 0)
            {
                return memo[node] = (nodeScore, new List<MapPoint>());
            }

            int maxChildScore = -1;
            List<MapPoint> bestChildren = new List<MapPoint>();

            foreach (var child in node.Children)
            {
                var result = Calculate(child);
                if (result.score > maxChildScore)
                {
                    maxChildScore = result.score;
                    bestChildren = new List<MapPoint> { child };
                }
                else if (result.score == maxChildScore && maxChildScore != -1)
                {
                    bestChildren.Add(child);
                }
            }

            // 如果所有的子节点分数都是 -1 (虽然不太可能)，保持为空
            return memo[node] = (nodeScore + (maxChildScore == -1 ? 0 : maxChildScore), bestChildren);
        }

        Calculate(start);

        var paths = new List<List<MapPoint>>();
        void Collect(MapPoint node, List<MapPoint> currentPath)
        {
            currentPath.Add(node);
            var nexts = memo[node].nexts;
            if (nexts.Count == 0)
            {
                paths.Add(new List<MapPoint>(currentPath));
            }
            else
            {
                foreach (var next in nexts)
                {
                    Collect(next, currentPath);
                }
            }
            currentPath.RemoveAt(currentPath.Count - 1);
        }

        if (memo.ContainsKey(start))
        {
            Collect(start, new List<MapPoint>());
        }

        return paths;
    }
}
