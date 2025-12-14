using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlacementManager : MonoBehaviour
{
    [Header("Config")]
    public BridgeConfig config;

    [Header("Runner POV Camera")]
    public bool runnerPOV = true;
    public Vector3 runnerCamOffset = new Vector3(0f, 1.2f, -2.5f); // behind & above runner
    public float runnerCamSmooth = 10f;
    public float runnerLookAhead = 3f;

    private Vector3 camOriginalPos;
    private Quaternion camOriginalRot;


    [Header("Scene References")]
    public Camera cam;
    public Collider stageCollider;
    public NodeComponent startNode;
    public NodeComponent endNode;

    [Header("Prefabs")]
    public GameObject deckPrefab;
    public GameObject nodePrefab;
    public GameObject pillarPrefab;
    public GameObject towerPrefab;
    public GameObject cablePrefab;

    // Runtime lists
    readonly List<NodeComponent> nodes = new();
    readonly List<DeckComponent> decks = new();
    readonly List<TowerComponent> towers = new();
    readonly List<GameObject> pillars = new();
    readonly List<GameObject> cables = new();

    private Vector3 lastStagePoint;
    private bool hasStagePoint;

    // -------- continuous chain state --------
    private NodeComponent chainNode;   // where the next Deck attaches
    private DeckComponent chainDeck;   // where the next Node attaches
    private int buildDir = +1;         // +1 = build to right, -1 = build to left

    //  hard stop once connected to end
    private bool bridgeComplete;

    // Undo
    struct UndoRecord
    {
        public GameObject spawned;     // can be null for "connect to end"
        public Action undoState;
        public Action undoLists;
    }
    readonly Stack<UndoRecord> undo = new();

    GameObject runner;

    void Awake()
    {
        if (!cam) cam = Camera.main;
    }

    IEnumerator Start()
    {
        // Wait so StageSetup positions permanent nodes first
        yield return null;

        if (!ValidateBasics()) yield break;

        bridgeComplete = false;

        // Choose build direction based on where End is relative to Start.
        buildDir = (endNode.transform.position.x >= startNode.transform.position.x) ? +1 : -1;

        // Lock permanent node sides
        EnforcePermanentNodeRules();

        // Seed lists
        nodes.Clear();
        if (startNode) nodes.Add(startNode);
        if (endNode && endNode != startNode) nodes.Add(endNode);

        // Initialize chain at Start
        chainNode = startNode;
        chainDeck = null;

        Debug.Log($"[PlacementManager] Initialized. buildDir={(buildDir > 0 ? "RIGHT" : "LEFT")}");
    }

    void Update()
    {
        if (!cam || !stageCollider) return;

        Vector2 mousePos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        Ray ray = cam.ScreenPointToRay(mousePos);

        if (stageCollider.Raycast(ray, out RaycastHit hit, 10000f))
        {
            lastStagePoint = hit.point;
            hasStagePoint = true;
        }
    }

    Vector3 GetPlacementPoint()
    {
        if (hasStagePoint) return lastStagePoint;
        if (startNode) return startNode.transform.position;
        return Vector3.zero;
    }

    void EnforcePermanentNodeRules()
    {
        // Permanent nodes are special:
        // Start can only build forward, End can only accept from forward.
        if (!startNode || !endNode) return;

        if (buildDir > 0)
        {
            // Building to the RIGHT:
            startNode.leftFree = false; // start left is never allowed
            endNode.rightFree = false;  // end right is never allowed

            // make sure the "allowed" side is actually usable if unconnected
            if (startNode.rightDeck == null) startNode.rightFree = true;
            if (endNode.leftDeck == null) endNode.leftFree = true;
        }
        else
        {
            // Building to the LEFT:
            startNode.rightFree = false;
            endNode.leftFree = false;

            if (startNode.leftDeck == null) startNode.leftFree = true;
            if (endNode.rightDeck == null) endNode.rightFree = true;
        }
    }

    bool ForwardSideFree(NodeComponent n) => (buildDir > 0) ? n.rightFree : n.leftFree;

    // -------------------- BUILD BUTTONS (CONTINUOUS) --------------------

    public void SpawnDeck()
    {
        if (!ValidateBasics()) return;

        // stop if completed
        if (bridgeComplete || chainNode == endNode)
        {
            Debug.LogWarning("[SpawnDeck] Bridge already complete.");
            return;
        }

        if (!chainNode) { Debug.LogWarning("[SpawnDeck] Chain node missing."); return; }

        // Must place Node after placing a Deck (to keep it continuous)
        if (chainDeck != null)
        {
            Debug.LogWarning("[SpawnDeck] Place a Node next (you already placed a Deck).");
            return;
        }

        // Can't build forward if forward side is blocked
        if (!ForwardSideFree(chainNode))
        {
            Debug.LogWarning("[SpawnDeck] No forward side free on current chain node.");
            return;
        }

        GameObject go = Instantiate(deckPrefab, transform);
        DeckComponent deck = go.GetComponent<DeckComponent>();
        if (!deck)
        {
            Debug.LogError("[SpawnDeck] Deck prefab missing DeckComponent.");
            Destroy(go);
            return;
        }

        deck.ApplySize(config.deckSize);
        decks.Add(deck);

        // Save for undo
        var oldChainNode = chainNode;
        var oldChainDeck = chainDeck;
        bool oldBridgeComplete = bridgeComplete;

        bool oldNodeL = chainNode.leftFree, oldNodeR = chainNode.rightFree;
        DeckComponent oldNodeLeftDeck = chainNode.leftDeck;
        DeckComponent oldNodeRightDeck = chainNode.rightDeck;

        bool oldDeckL = deck.leftFree, oldDeckR = deck.rightFree;
        NodeComponent oldDeckLeftNode = deck.leftNode;
        NodeComponent oldDeckRightNode = deck.rightNode;

        float dx = config.nodeSize.x * 0.5f + config.deckSize.x * 0.5f;
        Vector3 pos = chainNode.transform.position;

        if (buildDir > 0)
        {
            // Deck to the RIGHT of chain node
            pos.x += dx;

            chainNode.rightFree = false;
            deck.leftFree = false;

            chainNode.rightDeck = deck;
            deck.leftNode = chainNode;
        }
        else
        {
            // Deck to the LEFT of chain node
            pos.x -= dx;

            chainNode.leftFree = false;
            deck.rightFree = false;

            chainNode.leftDeck = deck;
            deck.rightNode = chainNode;
        }

        go.transform.position = new Vector3(pos.x, chainNode.transform.position.y, chainNode.transform.position.z);

        // Now we must place a Node on this deck
        chainDeck = deck;

        undo.Push(new UndoRecord
        {
            spawned = go,
            undoState = () =>
            {
                bridgeComplete = oldBridgeComplete;

                chainNode = oldChainNode;
                chainDeck = oldChainDeck;

                oldChainNode.leftFree = oldNodeL;
                oldChainNode.rightFree = oldNodeR;
                oldChainNode.leftDeck = oldNodeLeftDeck;
                oldChainNode.rightDeck = oldNodeRightDeck;

                deck.leftFree = oldDeckL;
                deck.rightFree = oldDeckR;
                deck.leftNode = oldDeckLeftNode;
                deck.rightNode = oldDeckRightNode;

                EnforcePermanentNodeRules();
            },
            undoLists = () => decks.Remove(deck)
        });
    }

    public void SpawnNode()
    {
        if (!ValidateBasics()) return;

        //  stop if completed
        if (bridgeComplete || chainNode == endNode)
        {
            Debug.LogWarning("[SpawnNode] Bridge already complete.");
            return;
        }

        // Must have a deck waiting
        if (chainDeck == null)
        {
            Debug.LogWarning("[SpawnNode] Place a Deck first.");
            return;
        }

        // Compute expected node position in the forward direction
        float dx = config.nodeSize.x * 0.5f + config.deckSize.x * 0.5f;
        Vector3 expected = chainDeck.transform.position;
        expected.x += (buildDir > 0) ? dx : -dx;
        expected.y = chainDeck.transform.position.y;
        expected.z = chainDeck.transform.position.z;

        //  connect to End if the next step would REACH or PASS the End within one step
        float eps = 0.05f * config.unit;
        if (CanConnectToEnd(expected, dx, eps))
        {
            ConnectDeckToEnd(dx, eps);
            return;
        }

        // Otherwise: spawn a new node normally
        GameObject go = Instantiate(nodePrefab, transform);
        NodeComponent node = go.GetComponent<NodeComponent>();
        if (!node)
        {
            Debug.LogError("[SpawnNode] Node prefab missing NodeComponent.");
            Destroy(go);
            return;
        }

        go.transform.localScale = config.nodeSize;
        nodes.Add(node);

        // Save for undo
        var oldChainNode = chainNode;
        var oldChainDeck = chainDeck;
        bool oldBridgeComplete = bridgeComplete;

        bool oldDeckL = chainDeck.leftFree, oldDeckR = chainDeck.rightFree;
        NodeComponent oldDeckLeftNode = chainDeck.leftNode;
        NodeComponent oldDeckRightNode = chainDeck.rightNode;

        bool oldNodeL = node.leftFree, oldNodeR = node.rightFree;
        DeckComponent oldNodeLeftDeck = node.leftDeck;
        DeckComponent oldNodeRightDeck = node.rightDeck;

        if (buildDir > 0)
        {
            // Node to the RIGHT of deck: node.left touches deck.right
            chainDeck.rightFree = false;
            node.leftFree = false;

            chainDeck.rightNode = node;
            node.leftDeck = chainDeck;
        }
        else
        {
            // Node to the LEFT of deck: node.right touches deck.left
            chainDeck.leftFree = false;
            node.rightFree = false;

            chainDeck.leftNode = node;
            node.rightDeck = chainDeck;
        }

        go.transform.position = expected;

        // Advance chain head
        chainNode = node;
        chainDeck = null;

        undo.Push(new UndoRecord
        {
            spawned = go,
            undoState = () =>
            {
                bridgeComplete = oldBridgeComplete;

                chainNode = oldChainNode;
                chainDeck = oldChainDeck;

                oldChainDeck.leftFree = oldDeckL;
                oldChainDeck.rightFree = oldDeckR;
                oldChainDeck.leftNode = oldDeckLeftNode;
                oldChainDeck.rightNode = oldDeckRightNode;

                node.leftFree = oldNodeL;
                node.rightFree = oldNodeR;
                node.leftDeck = oldNodeLeftDeck;
                node.rightDeck = oldNodeRightDeck;

                EnforcePermanentNodeRules();
            },
            undoLists = () => nodes.Remove(node)
        });
    }

    // robust End connection (doesn't require exact X equality)
    bool CanConnectToEnd(Vector3 expectedNodePos, float stepDx, float eps)
    {
        if (!endNode) return false;

        float endX = endNode.transform.position.x;
        float deckX = chainDeck ? chainDeck.transform.position.x : expectedNodePos.x;

        if (buildDir > 0)
        {
            // remaining distance from deck to end along +X
            float remaining = endX - deckX;

            // if next node would reach/pass end (within one step + eps)
            if (remaining <= stepDx + eps)
                return endNode.leftFree; // approaching from left
        }
        else
        {
            float remaining = deckX - endX;
            if (remaining <= stepDx + eps)
                return endNode.rightFree; // approaching from right
        }

        return false;
    }

    // after connecting, HARD STOP further build
    void ConnectDeckToEnd(float stepDx, float eps)
    {
        if (!chainDeck) return;

        var oldChainNode = chainNode;
        var oldChainDeck = chainDeck;
        bool oldBridgeComplete = bridgeComplete;

        bool oldEndL = endNode.leftFree;
        bool oldEndR = endNode.rightFree;
        DeckComponent oldEndLeftDeck = endNode.leftDeck;
        DeckComponent oldEndRightDeck = endNode.rightDeck;

        bool oldDeckL = chainDeck.leftFree;
        bool oldDeckR = chainDeck.rightFree;
        NodeComponent oldDeckLeftNode = chainDeck.leftNode;
        NodeComponent oldDeckRightNode = chainDeck.rightNode;

        if (buildDir > 0)
        {
            // connect deck.right -> end.left
            chainDeck.rightFree = false;
            endNode.leftFree = false;

            chainDeck.rightNode = endNode;
            endNode.leftDeck = chainDeck;
        }
        else
        {
            // connect deck.left -> end.right
            chainDeck.leftFree = false;
            endNode.rightFree = false;

            chainDeck.leftNode = endNode;
            endNode.rightDeck = chainDeck;
        }

        chainNode = endNode;
        chainDeck = null;

        bridgeComplete = true;

        undo.Push(new UndoRecord
        {
            spawned = null,
            undoState = () =>
            {
                bridgeComplete = oldBridgeComplete;

                chainNode = oldChainNode;
                chainDeck = oldChainDeck;

                endNode.leftFree = oldEndL;
                endNode.rightFree = oldEndR;
                endNode.leftDeck = oldEndLeftDeck;
                endNode.rightDeck = oldEndRightDeck;

                oldChainDeck.leftFree = oldDeckL;
                oldChainDeck.rightFree = oldDeckR;
                oldChainDeck.leftNode = oldDeckLeftNode;
                oldChainDeck.rightNode = oldDeckRightNode;

                EnforcePermanentNodeRules();
            },
            undoLists = null
        });

        Debug.Log("Connected to EndNode! Bridge complete.");
    }

    // -------------------- SUPPORTS (still nearest-node based) --------------------

    public void SpawnPillar()
    {
        if (!ValidateBasics()) return;

        Vector3 cursor = GetPlacementPoint();
        NodeComponent target = FindNearestNode(cursor, n => n && n.downFree);
        if (!target)
        {
            Debug.LogWarning("[SpawnPillar] No valid node found (need downFree).");
            return;
        }

        GameObject go = Instantiate(pillarPrefab, transform);
        go.transform.localScale = config.pillarSize;

        Vector3 pos = target.transform.position;
        pos.y = target.transform.position.y - config.nodeSize.y * 0.5f - config.pillarSize.y * 0.5f;
        go.transform.position = pos;

        bool oldDown = target.downFree;
        target.downFree = false;
        pillars.Add(go);

        undo.Push(new UndoRecord
        {
            spawned = go,
            undoState = () => target.downFree = oldDown,
            undoLists = () => pillars.Remove(go)
        });
    }

    // Two towers per node: top front (+Z) and top back (-Z)
    public void SpawnTower()
    {
        if (!ValidateBasics()) return;

        Vector3 cursor = GetPlacementPoint();
        NodeComponent target = FindNearestNode(cursor, n => n && (n.topFrontFree || n.topBackFree));
        if (!target)
        {
            Debug.LogWarning("[SpawnTower] No valid node found (need topFrontFree or topBackFree).");
            return;
        }

        GameObject group = new GameObject($"TowerPair_{target.name}");
        group.transform.SetParent(transform);

        bool oldFront = target.topFrontFree;
        bool oldBack = target.topBackFree;

        List<TowerComponent> added = new();

        float y = target.transform.position.y + config.nodeSize.y * 0.5f + config.towerSize.y * 0.5f;
        float zOffset = (config.nodeSize.z * 0.5f) - (config.towerSize.z * 0.5f);

        if (target.topFrontFree)
        {
            var goF = Instantiate(towerPrefab, group.transform);
            var tf = goF.GetComponent<TowerComponent>();
            if (!tf) { Destroy(group); Debug.LogError("[SpawnTower] Tower prefab missing TowerComponent."); return; }

            tf.ApplySize(config.towerSize);
            towers.Add(tf);
            added.Add(tf);

            goF.transform.position = new Vector3(target.transform.position.x, y, target.transform.position.z + zOffset);
            target.topFrontFree = false;
        }

        if (target.topBackFree)
        {
            var goB = Instantiate(towerPrefab, group.transform);
            var tb = goB.GetComponent<TowerComponent>();
            if (!tb) { Destroy(group); Debug.LogError("[SpawnTower] Tower prefab missing TowerComponent."); return; }

            tb.ApplySize(config.towerSize);
            towers.Add(tb);
            added.Add(tb);

            goB.transform.position = new Vector3(target.transform.position.x, y, target.transform.position.z - zOffset);
            target.topBackFree = false;
        }

        if (added.Count == 0)
        {
            Destroy(group);
            Debug.LogWarning("[SpawnTower] No towers spawned (both edges used).");
            return;
        }

        undo.Push(new UndoRecord
        {
            spawned = group,
            undoState = () =>
            {
                target.topFrontFree = oldFront;
                target.topBackFree = oldBack;
            },
            undoLists = () =>
            {
                foreach (var t in added) towers.Remove(t);
            }
        });
    }

    // -------------------- SUSPENDERS --------------------

    public void SpawnSuspenders()
    {
        if (!ValidateBasics()) return;

        GameObject group = new GameObject("SuspenderBatch_Horizontal");
        group.transform.SetParent(transform);

        List<GameObject> spawned = new();

        foreach (var deck in decks)
        {
            if (!deck) continue;

            // Must have 2 nodes (left and right side of this deck)
            NodeComponent a = deck.leftNode;
            NodeComponent b = deck.rightNode;
            if (!a || !b) continue;

            // Determine physical left/right by world X
            NodeComponent leftNode = (a.transform.position.x <= b.transform.position.x) ? a : b;
            NodeComponent rightNode = (leftNode == a) ? b : a;

            // Do both lanes: front (+Z) and back (-Z)
            foreach (int zSign in new[] { +1, -1 })
            {
                TowerComponent leftTower = FindTowerOnNodeEdge(leftNode, zSign);
                TowerComponent rightTower = FindTowerOnNodeEdge(rightNode, zSign);

                if (!leftTower || !rightTower) continue;

                // left tower faces right (+X), right tower faces left (-X)
                Vector3 pL = GetTowerTopCornerFacingX(leftTower, +1, zSign);
                Vector3 pR = GetTowerTopCornerFacingX(rightTower, -1, zSign);

                // Force perfectly horizontal line (same Y)
                float y = Mathf.Max(pL.y, pR.y);
                pL.y = y;
                pR.y = y;

                // Skip tiny/invalid lines
                if ((pL - pR).sqrMagnitude < 0.000001f) continue;

                GameObject cable = SpawnCable(pL, pR);
                cable.transform.SetParent(group.transform, true);

                cables.Add(cable);
                spawned.Add(cable);
            }
        }

        if (spawned.Count == 0)
        {
            Destroy(group);
            Debug.Log("No horizontal suspenders created (missing towers on both sides).");
            return;
        }

        // Optional undo support
        undo.Push(new UndoRecord
        {
            spawned = group,
            undoState = null,
            undoLists = () => { foreach (var c in spawned) cables.Remove(c); }
        });

        Debug.Log($"Spawned {spawned.Count} horizontal suspenders.");
    }



    public void UndoLast()
    {
        if (undo.Count == 0) return;

        var rec = undo.Pop();
        rec.undoState?.Invoke();
        rec.undoLists?.Invoke();
        if (rec.spawned) Destroy(rec.spawned);
    }

    public void Simulate()
    {
        if (!ValidateBasics()) return;

        if (!IsBridgeComplete(out var waypoints))
        {
            Debug.Log("Bridge incomplete (Start is not connected to End).");
            return;
        }

        if (!runner)
        {
            runner = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            runner.name = "Runner";
            runner.transform.localScale = Vector3.one * (0.5f * config.unit);
        }

        if (runnerPOV && cam)
        {
            camOriginalPos = cam.transform.position;
            camOriginalRot = cam.transform.rotation;
        }


        StopAllCoroutines();
        StartCoroutine(RunPath(waypoints));
    }

    // -------------------- HELPERS --------------------

    Vector3 GetTowerTopCornerFacingX(TowerComponent tower, int faceXSign, int zSign)
    {
        // Best: use renderer bounds (accounts for scaling + rotation)
        var r = tower.GetComponentInChildren<Renderer>();
        if (r != null)
        {
            Bounds b = r.bounds;

            float x = (faceXSign > 0) ? b.max.x : b.min.x;   // facing toward other tower
            float y = b.max.y;                               // top
            float z = (zSign > 0) ? b.max.z : b.min.z;       // front lane / back lane

            return new Vector3(x, y, z);
        }

        // Fallback: use transform scale if no renderer exists
        Vector3 c = tower.transform.position;
        Vector3 half = tower.transform.lossyScale * 0.5f;

        float fx = c.x + faceXSign * half.x;
        float fy = c.y + half.y;
        float fz = c.z + zSign * half.z;

        return new Vector3(fx, fy, fz);
    }

    float GetDeckTopY(DeckComponent deck)
    {
        var r = deck.GetComponentInChildren<Renderer>();
        if (r) return r.bounds.max.y;

        // Fallback if no Renderer exists
        return deck.transform.position.y + (config.deckSize.y * 0.5f);
    }

    bool ValidateBasics()
    {
        if (!config) { Debug.LogError("[PlacementManager] Missing BridgeConfig."); return false; }
        if (!cam) { cam = Camera.main; if (!cam) { Debug.LogError("[PlacementManager] Missing Camera."); return false; } }
        if (!stageCollider) { Debug.LogError("[PlacementManager] Missing Stage Collider."); return false; }
        if (!startNode || !endNode) { Debug.LogError("[PlacementManager] Missing StartNode or EndNode reference."); return false; }
        if (!deckPrefab || !nodePrefab || !pillarPrefab || !towerPrefab || !cablePrefab)
        {
            Debug.LogError("[PlacementManager] One or more prefabs are not assigned.");
            return false;
        }
        return true;
    }

    NodeComponent FindNearestNode(Vector3 cursor, Func<NodeComponent, bool> predicate)
    {
        NodeComponent best = null;
        float bestD = float.PositiveInfinity;

        foreach (var n in nodes)
        {
            if (!n) continue;
            if (!predicate(n)) continue;
            float d = (n.transform.position - cursor).sqrMagnitude;
            if (d < bestD) { bestD = d; best = n; }
        }
        return best;
    }

    struct CornerRef
    {
        public Vector3 pos;
        public Func<bool> get;
        public Action<bool> set;
    }

    //  world-space line renderer + clamped thickness
    GameObject SpawnCable(Vector3 a, Vector3 b)
    {
        var go = Instantiate(cablePrefab, transform);

        // reset transform so prefab scale doesn't mess things up
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var lr = go.GetComponent<LineRenderer>();
        if (lr == null)
        {
            Debug.LogError("[SpawnCable] cablePrefab needs a LineRenderer.");
            return go;
        }

        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);

        // Make it clearly visible
        lr.alignment = LineAlignment.View;
        lr.numCapVertices = 6;

        // Thickness: bump it up hard (minimum thickness too)
        float w = Mathf.Max(config.cableRadius * 6f, 0.015f * config.unit);
        lr.startWidth = w;
        lr.endWidth = w;

        return go;
    }



    enum CornerId { FL, FR, BL, BR }

    TowerComponent FindTowerOnNodeEdge(NodeComponent node, int zSign)
    {
        if (!node) return null;

        float y = node.transform.position.y + (config.nodeSize.y * 0.5f) + (config.towerSize.y * 0.5f);
        float zOffset = (config.nodeSize.z * 0.5f) - (config.towerSize.z * 0.5f);

        Vector3 expected = new Vector3(
            node.transform.position.x,
            y,
            node.transform.position.z + zSign * zOffset
        );

        float tol = Mathf.Max(0.1f * config.unit, 0.25f * config.towerSize.x, 0.25f * config.towerSize.z);
        float best = tol * tol;

        TowerComponent bestTower = null;
        foreach (var t in towers)
        {
            if (!t) continue;
            float d = (t.transform.position - expected).sqrMagnitude;
            if (d < best)
            {
                best = d;
                bestTower = t;
            }
        }
        return bestTower;
    }

    //  get "true" corner positions from renderer bounds 
    bool TryGetBoundsCorner(GameObject root, CornerId id, bool top, out Vector3 pos)
    {
        pos = default;
        if (!root) return false;

        var r = root.GetComponentInChildren<Renderer>();
        if (!r) return false;

        Bounds b = r.bounds;
        float y = top ? b.max.y : b.min.y;

        switch (id)
        {
            case CornerId.FL: pos = new Vector3(b.min.x, y, b.max.z); return true;
            case CornerId.FR: pos = new Vector3(b.max.x, y, b.max.z); return true;
            case CornerId.BL: pos = new Vector3(b.min.x, y, b.min.z); return true;
            case CornerId.BR: pos = new Vector3(b.max.x, y, b.min.z); return true;
        }
        return false;
    }

    void TryAddSlopeSuspender(
        DeckComponent deck,
        CornerId deckCornerId,
        TowerComponent tower,
        CornerId towerCornerId,
        Transform parent,
        List<GameObject> spawned,
        List<Action> restoreStates)
    {
        if (!TryGetCorner(deck, deckCornerId, out CornerRef dCorner)) return;
        if (!TryGetCorner(tower, towerCornerId, out CornerRef tCorner)) return;

        // prevent duplicates
        if (!dCorner.get() || !tCorner.get()) return;

        //  use bounds for accurate physical corners
        if (!TryGetBoundsCorner(tower.gameObject, towerCornerId, top: true, out Vector3 towerTopCorner))
            towerTopCorner = tCorner.pos;

        if (!TryGetBoundsCorner(deck.gameObject, deckCornerId, top: true, out Vector3 deckTopCorner))
            deckTopCorner = dCorner.pos;

        //  tower bottom Y from bounds (not lossyScale)
        float towerBottomY = towerTopCorner.y;
        {
            var tr = tower.GetComponentInChildren<Renderer>();
            if (tr) towerBottomY = tr.bounds.min.y;
            else towerBottomY = tower.transform.position.y - (tower.transform.lossyScale.y * 0.5f);
        }

        Vector3 start = towerTopCorner;
        Vector3 end = deckTopCorner;

        // slope rule:
        // - deck end point is at tower bottom Y
        // - same Z lane as the tower corner
        end.y = towerBottomY;
        end.z = start.z;

        GameObject cable = SpawnCable(start, end);
        cable.transform.SetParent(parent, true);

        bool oldD = dCorner.get();
        bool oldT = tCorner.get();

        dCorner.set(false);
        tCorner.set(false);

        cables.Add(cable);
        spawned.Add(cable);

        restoreStates.Add(() =>
        {
            dCorner.set(oldD);
            tCorner.set(oldT);
        });
    }

    bool TryGetCorner(DeckComponent d, CornerId id, out CornerRef c)
    {
        c = default;
        if (!d) return false;

        switch (id)
        {
            case CornerId.FL:
                if (!d.cornerFL) return false;
                c = new CornerRef { pos = d.cornerFL.position, get = () => d.cornerFL_Free, set = v => d.cornerFL_Free = v };
                return true;
            case CornerId.FR:
                if (!d.cornerFR) return false;
                c = new CornerRef { pos = d.cornerFR.position, get = () => d.cornerFR_Free, set = v => d.cornerFR_Free = v };
                return true;
            case CornerId.BL:
                if (!d.cornerBL) return false;
                c = new CornerRef { pos = d.cornerBL.position, get = () => d.cornerBL_Free, set = v => d.cornerBL_Free = v };
                return true;
            case CornerId.BR:
                if (!d.cornerBR) return false;
                c = new CornerRef { pos = d.cornerBR.position, get = () => d.cornerBR_Free, set = v => d.cornerBR_Free = v };
                return true;
        }
        return false;
    }

    bool TryGetCorner(TowerComponent t, CornerId id, out CornerRef c)
    {
        c = default;
        if (!t) return false;

        switch (id)
        {
            case CornerId.FL:
                if (!t.cornerFL) return false;
                c = new CornerRef { pos = t.cornerFL.position, get = () => t.cornerFL_Free, set = v => t.cornerFL_Free = v };
                return true;
            case CornerId.FR:
                if (!t.cornerFR) return false;
                c = new CornerRef { pos = t.cornerFR.position, get = () => t.cornerFR_Free, set = v => t.cornerFR_Free = v };
                return true;
            case CornerId.BL:
                if (!t.cornerBL) return false;
                c = new CornerRef { pos = t.cornerBL.position, get = () => t.cornerBL_Free, set = v => t.cornerBL_Free = v };
                return true;
            case CornerId.BR:
                if (!t.cornerBR) return false;
                c = new CornerRef { pos = t.cornerBR.position, get = () => t.cornerBR_Free, set = v => t.cornerBR_Free = v };
                return true;
        }
        return false;
    }

    bool IsBridgeComplete(out List<Vector3> waypoints)
    {
        waypoints = new List<Vector3>();

        var prev = new Dictionary<NodeComponent, (NodeComponent fromNode, DeckComponent viaDeck)>();
        var q = new Queue<NodeComponent>();
        var visited = new HashSet<NodeComponent>();

        q.Enqueue(startNode);
        visited.Add(startNode);

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur == endNode) break;

            foreach (var deck in new[] { cur.leftDeck, cur.rightDeck })
            {
                if (!deck) continue;

                NodeComponent next = null;
                if (deck.leftNode == cur) next = deck.rightNode;
                else if (deck.rightNode == cur) next = deck.leftNode;

                if (!next) continue;
                if (visited.Contains(next)) continue;

                visited.Add(next);
                prev[next] = (cur, deck);
                q.Enqueue(next);
            }
        }

        if (!visited.Contains(endNode))
            return false;

        var nodePath = new List<NodeComponent>();
        var n = endNode;
        nodePath.Add(n);

        while (n != startNode)
        {
            var p = prev[n].fromNode;
            n = p;
            nodePath.Add(n);
        }
        nodePath.Reverse();

        waypoints.Add(nodePath[0].transform.position);
        for (int i = 1; i < nodePath.Count; i++)
        {
            var to = nodePath[i];
            var info = prev[to];
            if (info.viaDeck) waypoints.Add(info.viaDeck.transform.position);
            waypoints.Add(to.transform.position);
        }

        return true;
    }

    IEnumerator RunPath(List<Vector3> points)
    {
        float speed = 8f * config.unit;
        float yLift = (config.nodeSize.y * 0.5f) + (0.35f * config.unit);

        for (int i = 0; i < points.Count; i++)
            points[i] = new Vector3(points[i].x, points[i].y + yLift, points[i].z);

        runner.transform.position = points[0];

        for (int i = 1; i < points.Count; i++)
        {
            Vector3 segStart = runner.transform.position;
            Vector3 segEnd = points[i];

            float dist = Vector3.Distance(segStart, segEnd);
            float t = 0f;

            while (t < 1f)
            {
                t += Time.deltaTime * (speed / Mathf.Max(0.001f, dist));
                float tt = Mathf.Clamp01(t);

                Vector3 pos = Vector3.Lerp(segStart, segEnd, tt);
                runner.transform.position = pos;

                // ----- Runner POV camera -----
                if (runnerPOV && cam)
                {
                    Vector3 forwardDir = (segEnd - segStart);
                    if (forwardDir.sqrMagnitude < 0.000001f) forwardDir = runner.transform.forward;
                    forwardDir.Normalize();

                    Vector3 desiredCamPos =
                        runner.transform.position
                        + (Quaternion.LookRotation(forwardDir) * runnerCamOffset);

                    cam.transform.position = Vector3.Lerp(
                        cam.transform.position,
                        desiredCamPos,
                        1f - Mathf.Exp(-runnerCamSmooth * Time.deltaTime)
                    );

                    Vector3 lookTarget = runner.transform.position + forwardDir * runnerLookAhead;
                    Quaternion desiredRot = Quaternion.LookRotation(lookTarget - cam.transform.position, Vector3.up);

                    cam.transform.rotation = Quaternion.Slerp(
                        cam.transform.rotation,
                        desiredRot,
                        1f - Mathf.Exp(-runnerCamSmooth * Time.deltaTime)
                    );
                }

                yield return null;
            }
        }

        Debug.Log("Crossed successfully!");

        // restore camera after finishing
        if (runnerPOV && cam)
        {
            cam.transform.position = camOriginalPos;
            cam.transform.rotation = camOriginalRot;
        }
    }
}






