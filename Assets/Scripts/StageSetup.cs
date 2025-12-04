using UnityEngine;

public class StageSetup : MonoBehaviour
{
    public BridgeConfig config;
    public Transform startNode;
    public Transform endNode;

    void Start()
    {
        float span = config.stageSpan;
        float y = config.permanentNodeCenterY;

        startNode.position = new Vector3(-span * 0.5f, y, 0f);
        endNode.position = new Vector3(span * 0.5f, y, 0f);

        startNode.localScale = config.nodeSize;
        endNode.localScale = config.nodeSize;

        startNode.GetComponent<NodeComponent>().isPermanent = true;
        endNode.GetComponent<NodeComponent>().isPermanent = true;
    }
}
