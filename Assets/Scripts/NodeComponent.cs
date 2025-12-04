using UnityEngine;

public class NodeComponent : MonoBehaviour
{
    public bool isPermanent;

    // Horizontal building connections
    public bool leftFree = true;
    public bool rightFree = true;

    // Supports
    public bool downFree = true;

    // Two tower slots on the TOP edges (+Z and -Z)
    public bool topFrontFree = true; // +Z edge
    public bool topBackFree = true; // -Z edge

    // Compatibility: "any top edge free?"
    public bool upFree => topFrontFree || topBackFree;

    // Connections used for traversal/sim
    public DeckComponent leftDeck;
    public DeckComponent rightDeck;
}
