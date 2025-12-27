# Testing Plan for Construction Carving Feature

## Test Environment Setup

### Prerequisites
1. Unity project with ProceduralTerrainGenerator configured
2. At least two Unity layers:
   - "Terrain" layer (for terrain chunks)
   - "Construction" layer (for buildings/structures)
3. Test objects:
   - Building prefab with BoxCollider on "Construction" layer
   - Tree prefab with CapsuleCollider on "Default" layer
   - Tagged building prefab with tag "Building" on "Construction" layer

## Test Cases

### Test 1: Basic Layer Filtering
**Objective**: Verify only construction layer objects carve terrain

**Setup**:
1. Set `constructionLayerMask` to only include "Construction" layer
2. Leave `requiredCarvingTag` empty
3. Set `constructionCarveStrength` to 10.0

**Steps**:
1. Place a building prefab (on Construction layer) on terrain
2. Place a tree prefab (on Default layer) on terrain
3. Regenerate terrain chunks in those areas

**Expected Result**:
- ✅ Terrain should carve around the building
- ✅ Terrain should NOT carve around the tree
- ✅ Tree remains on terrain surface without any carving

### Test 2: Tag Filtering
**Objective**: Verify optional tag filtering works correctly

**Setup**:
1. Set `constructionLayerMask` to include "Construction" layer
2. Set `requiredCarvingTag` to "Building"
3. Create two objects on Construction layer:
   - Object A: Tagged "Building"
   - Object B: Not tagged or different tag

**Steps**:
1. Place both objects on terrain
2. Regenerate terrain chunks

**Expected Result**:
- ✅ Terrain carves around Object A (has required tag)
- ✅ Terrain does NOT carve around Object B (missing required tag)

### Test 3: Combined Layer + Tag Filtering
**Objective**: Verify both filters must pass

**Setup**:
1. Set `constructionLayerMask` to "Construction" layer only
2. Set `requiredCarvingTag` to "Building"
3. Create test matrix:
   - Object A: Construction layer + "Building" tag
   - Object B: Construction layer + no tag
   - Object C: Default layer + "Building" tag
   - Object D: Default layer + no tag

**Steps**:
1. Place all four objects on terrain
2. Regenerate terrain

**Expected Result**:
- ✅ Only Object A carves terrain (passes both filters)
- ✅ Objects B, C, D do not carve terrain

### Test 4: Carve Strength Adjustment
**Objective**: Verify carve strength parameter works

**Setup**:
1. Configure layer mask to include Construction layer
2. Test with different strength values: 5.0, 10.0, 20.0

**Steps**:
1. Place identical building prefab at three locations
2. Set strength to 5.0, regenerate first chunk
3. Set strength to 10.0, regenerate second chunk
4. Set strength to 20.0, regenerate third chunk

**Expected Result**:
- ✅ Higher strength creates more aggressive carving (larger carved area)
- ✅ Lower strength creates gentler carving (smaller carved area)
- ✅ All three should carve, but with different intensities

### Test 5: Cache Refresh on Placement
**Objective**: Verify automatic cache refresh when placing via ConstructionSystem

**Setup**:
1. Configure carving as in Test 1
2. Enable build mode in ConstructionSystem

**Steps**:
1. Place a building using ConstructionSystem (press B to toggle, click to place)
2. Wait 0.5 seconds
3. Regenerate nearby terrain chunks

**Expected Result**:
- ✅ Newly placed building appears in carving list immediately
- ✅ Terrain carves around new building without waiting 1 second cache refresh

### Test 6: Manual Cache Refresh
**Objective**: Verify manual cache refresh API works

**Setup**:
1. Configure carving as in Test 1
2. Create a test script that places objects directly (not via ConstructionSystem)

**Test Script**:
```csharp
public class TestCacheRefresh : MonoBehaviour
{
    public GameObject buildingPrefab;
    public ProceduralTerrainGenerator generator;
    
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.P))
        {
            // Place building directly
            Instantiate(buildingPrefab, transform.position, Quaternion.identity);
            
            // Manually refresh cache
            if (generator != null && generator.Sampler != null)
            {
                generator.Sampler.RefreshCarvingColliderCache();
            }
        }
    }
}
```

**Steps**:
1. Press 'P' to place a building
2. Regenerate terrain chunks

**Expected Result**:
- ✅ Building appears in carving list immediately after manual refresh
- ✅ Terrain carves around new building

### Test 7: Performance Test
**Objective**: Verify caching provides performance benefits

**Setup**:
1. Scene with 50+ colliders (mix of construction and other objects)
2. Configure carving with Construction layer mask

**Steps**:
1. Profile chunk generation time with caching enabled
2. Note: Cache refreshes max once per second
3. Generate 10 chunks in rapid succession (< 1 second)

**Expected Result**:
- ✅ Only one FindObjectsOfType call occurs (at first chunk)
- ✅ Subsequent chunks use cached colliders
- ✅ Chunk generation remains smooth without per-chunk scene scans

### Test 8: Degenerate Collider Handling
**Objective**: Verify safety guards against zero-size colliders

**Setup**:
1. Create a GameObject on Construction layer with:
   - BoxCollider with size (0, 0, 0)
   - Or very tiny size (0.0001, 0.0001, 0.0001)

**Steps**:
1. Place the degenerate collider object
2. Regenerate terrain chunks

**Expected Result**:
- ✅ No division by zero error
- ✅ No NaN values in density calculations
- ✅ Degenerate collider is safely ignored or handled

### Test 9: Rotated Collider Carving
**Objective**: Verify carving works correctly with rotated objects

**Setup**:
1. Building prefab on Construction layer
2. Place at various rotations (0°, 45°, 90°, 180°)

**Steps**:
1. Place rotated buildings
2. Regenerate terrain

**Expected Result**:
- ✅ Terrain carves correctly oriented to building rotation
- ✅ Carved area matches building's oriented bounding box

## Validation Checklist

After running all tests:

- [ ] Only colliders on construction layer are carved
- [ ] Tag filtering works when configured
- [ ] Non-matching colliders are always skipped
- [ ] Carve strength parameter affects carving intensity
- [ ] Cache refresh works automatically (ConstructionSystem)
- [ ] Cache refresh works manually (API call)
- [ ] Performance is acceptable (no per-frame FindObjectsOfType)
- [ ] No division by zero with degenerate colliders
- [ ] Rotated objects carve correctly
- [ ] No security vulnerabilities (CodeQL clean)

## Known Limitations

1. **Cache Delay**: Objects placed outside ConstructionSystem won't carve until:
   - 1 second passes (automatic refresh), OR
   - Manual cache refresh is called

2. **FindObjectsOfType Cost**: Initial cache population scans all colliders in scene
   - Impact mitigated by 1-second refresh interval
   - Consider registration system for very large scenes (1000+ colliders)

3. **Static Assumption**: System assumes construction objects don't move frequently
   - Moving objects won't update terrain carving until cache refreshes
   - For dynamic scenarios, call RefreshCarvingColliderCache() after moves

## Troubleshooting Guide

| Issue | Possible Cause | Solution |
|-------|---------------|----------|
| Object not carving | Wrong layer | Check object is on Construction layer |
| Object not carving | Tag mismatch | Check object has required tag (if set) |
| Object not carving | Not regenerated | Regenerate affected terrain chunks |
| Object not carving | Cache not refreshed | Wait 1 second or call RefreshCarvingColliderCache() |
| Too much carving | Strength too high | Reduce constructionCarveStrength |
| Too little carving | Strength too low | Increase constructionCarveStrength |
| Wrong objects carving | Layer mask wrong | Check constructionLayerMask includes only Construction layer |
