using UnityEngine;

[CreateAssetMenu(fileName = "BridgeConfig", menuName = "Bridge Builder/Bridge Config")]
public class BridgeConfig : ScriptableObject
{
    [Min(0.01f)] public float unit = 1f;

    public Vector3 deckSize => new Vector3(8f, 1f, 6f) * unit;
    public Vector3 nodeSize => new Vector3(1f, 1f, 6f) * unit;
    public Vector3 pillarSize => new Vector3(1f, 6f, 1f) * unit;
    public Vector3 towerSize => new Vector3(1f, 6f, 1f) * unit;

    public float stageSpan => 80f * unit;

    // Your permanent-node height rule:
    public float permanentNodeCenterY => pillarSize.y + nodeSize.y * 0.5f;

    // Cable settings (these fix your error):
    public float cableRadius => 0.1f * unit;
    public float cableTargetDistance => 10f * unit;
    public float cableEpsilon => 0.05f * unit;
}
