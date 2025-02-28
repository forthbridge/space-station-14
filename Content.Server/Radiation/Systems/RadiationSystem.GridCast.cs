using System.Numerics;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Events;
using Content.Shared.Radiation.Components;
using Content.Shared.Radiation.Systems;
using Content.Shared.Stacks;
using Robust.Shared.Collections;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Radiation.Systems;

// main algorithm that fire radiation rays to target
public partial class RadiationSystem
{
    [Dependency] private readonly SharedStackSystem _stack = default!;

    private void UpdateGridcast()
    {
        // should we save debug information into rays?
        // if there is no debug sessions connected - just ignore it
        var saveVisitedTiles = _debugSessions.Count > 0;

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var sources = EntityQueryEnumerator<RadiationSourceComponent, TransformComponent>();
        var destinations = EntityQuery<RadiationReceiverComponent, TransformComponent>();
        var resistanceQuery = GetEntityQuery<RadiationGridResistanceComponent>();
        var transformQuery = GetEntityQuery<TransformComponent>();
        var gridQuery = GetEntityQuery<MapGridComponent>();
        var stackQuery = GetEntityQuery<StackComponent>();

        // precalculate world positions for each source
        // so we won't need to calc this in cycle over and over again
        var sourcesData = new ValueList<(EntityUid, RadiationSourceComponent, TransformComponent, Vector2)>();
        while (sources.MoveNext(out var uid, out var source, out var sourceTrs))
        {
            var worldPos = _transform.GetWorldPosition(sourceTrs, transformQuery);
            var data = (uid, source, sourceTrs, worldPos);
            sourcesData.Add(data);
        }

        // trace all rays from rad source to rad receivers
        var rays = new List<RadiationRay>();
        var receiversTotalRads = new ValueList<(RadiationReceiverComponent, float)>();
        foreach (var (dest, destTrs) in destinations)
        {
            var destWorld = _transform.GetWorldPosition(destTrs, transformQuery);

            var rads = 0f;
            foreach (var (uid, source, sourceTrs, sourceWorld) in sourcesData)
            {
                stackQuery.TryGetComponent(uid, out var stack);
                var intensity = source.Intensity * _stack.GetCount(uid, stack);

                // send ray towards destination entity
                var ray = Irradiate(uid, sourceTrs, sourceWorld,
                    destTrs.Owner, destTrs, destWorld,
                    intensity, source.Slope, saveVisitedTiles, resistanceQuery, transformQuery, gridQuery);
                if (ray == null)
                    continue;

                // save ray for debug
                rays.Add(ray);

                // add rads to total rad exposure
                if (ray.ReachedDestination)
                    rads += ray.Rads;
            }

            receiversTotalRads.Add((dest, rads));
        }

        // update information for debug overlay
        var elapsedTime = stopwatch.Elapsed.TotalMilliseconds;
        var totalSources = sourcesData.Count;
        var totalReceivers = receiversTotalRads.Count;
        UpdateGridcastDebugOverlay(elapsedTime, totalSources, totalReceivers, rays);

        // send rads to each entity
        foreach (var (receiver, rads) in receiversTotalRads)
        {
            // update radiation value of receiver
            // if no radiation rays reached target, that will set it to 0
            receiver.CurrentRadiation = rads;

            // also send an event with combination of total rad
            if (rads > 0)
                IrradiateEntity(receiver.Owner, rads,GridcastUpdateRate);
        }

        // raise broadcast event that radiation system has updated
        RaiseLocalEvent(new RadiationSystemUpdatedEvent());
    }

    private RadiationRay? Irradiate(EntityUid sourceUid, TransformComponent sourceTrs, Vector2 sourceWorld,
        EntityUid destUid, TransformComponent destTrs, Vector2 destWorld,
        float incomingRads, float slope, bool saveVisitedTiles,
        EntityQuery<RadiationGridResistanceComponent> resistanceQuery,
        EntityQuery<TransformComponent> transformQuery, EntityQuery<MapGridComponent> gridQuery)
    {
        // lets first check that source and destination on the same map
        if (sourceTrs.MapID != destTrs.MapID)
            return null;
        var mapId = sourceTrs.MapID;

        // get direction from rad source to destination and its distance
        var dir = destWorld - sourceWorld;
        var dist = dir.Length();

        // check if receiver is too far away
        if (dist > GridcastMaxDistance)
            return null;
        // will it even reach destination considering distance penalty
        var rads = incomingRads - slope * dist;
        if (rads <= MinIntensity)
            return null;

        // create a new radiation ray from source to destination
        // at first we assume that it doesn't hit any radiation blockers
        // and has only distance penalty
        var ray = new RadiationRay(mapId, sourceUid, sourceWorld, destUid, destWorld, rads);

        // if source and destination on the same grid it's possible that
        // between them can be another grid (ie. shuttle in center of donut station)
        // however we can do simplification and ignore that case
        if (GridcastSimplifiedSameGrid && sourceTrs.GridUid != null && sourceTrs.GridUid == destTrs.GridUid)
        {
            if (!gridQuery.TryGetComponent(sourceTrs.GridUid.Value, out var gridComponent))
                return ray;
            return Gridcast(gridComponent, ray, saveVisitedTiles, resistanceQuery, sourceTrs, destTrs, transformQuery.GetComponent(sourceTrs.GridUid.Value));
        }

        // lets check how many grids are between source and destination
        // do a box intersection test between target and destination
        // it's not very precise, but really cheap
        var box = Box2.FromTwoPoints(sourceWorld, destWorld);
        var grids = _mapManager.FindGridsIntersecting(mapId, box, true);

        // gridcast through each grid and try to hit some radiation blockers
        // the ray will be updated with each grid that has some blockers
        foreach (var grid in grids)
        {
            ray = Gridcast(grid, ray, saveVisitedTiles, resistanceQuery, sourceTrs, destTrs, transformQuery.GetComponent(grid.Owner));

            // looks like last grid blocked all radiation
            // we can return right now
            if (ray.Rads <= 0)
                return ray;
        }

        return ray;
    }

    private RadiationRay Gridcast(MapGridComponent grid, RadiationRay ray, bool saveVisitedTiles,
        EntityQuery<RadiationGridResistanceComponent> resistanceQuery,
        TransformComponent sourceTrs,
        TransformComponent destTrs,
        TransformComponent gridTrs)
    {
        var blockers = new List<(Vector2i, float)>();

        // if grid doesn't have resistance map just apply distance penalty
        var gridUid = grid.Owner;
        if (!resistanceQuery.TryGetComponent(gridUid, out var resistance))
            return ray;
        var resistanceMap = resistance.ResistancePerTile;

        // get coordinate of source and destination in grid coordinates

        // TODO Grid overlap. This currently assumes the grid is always parented directly to the map (local matrix == world matrix).
        // If ever grids are allowed to overlap, this might no longer be true. In that case, this should precompute and cache
        // inverse world matrices.

        Vector2 srcLocal = sourceTrs.ParentUid == grid.Owner
            ? sourceTrs.LocalPosition
            : gridTrs.InvLocalMatrix.Transform(ray.Source);

        Vector2 dstLocal = destTrs.ParentUid == grid.Owner
            ? destTrs.LocalPosition
            : gridTrs.InvLocalMatrix.Transform(ray.Destination);

        Vector2i sourceGrid = new(
            (int) Math.Floor(srcLocal.X / grid.TileSize),
            (int) Math.Floor(srcLocal.Y / grid.TileSize));

        Vector2i destGrid = new(
            (int) Math.Floor(dstLocal.X / grid.TileSize),
            (int) Math.Floor(dstLocal.Y / grid.TileSize));

        // iterate tiles in grid line from source to destination
        var line = new GridLineEnumerator(sourceGrid, destGrid);
        while (line.MoveNext())
        {
            var point = line.Current;
            if (!resistanceMap.TryGetValue(point, out var resData))
                continue;
            ray.Rads -= resData;

            // save data for debug
            if (saveVisitedTiles)
                blockers.Add((point, ray.Rads));

            // no intensity left after blocker
            if (ray.Rads <= MinIntensity)
            {
                ray.Rads = 0;
                break;
            }
        }

        // save data for debug if needed
        if (saveVisitedTiles && blockers.Count > 0)
            ray.Blockers.Add(gridUid, blockers);

        return ray;
    }
}
