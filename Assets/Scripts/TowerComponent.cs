using UnityEngine;

public class TowerComponent : MonoBehaviour
{
    public bool cornerFL_Free = true, cornerFR_Free = true, cornerBL_Free = true, cornerBR_Free = true;
    public Transform cornerFL, cornerFR, cornerBL, cornerBR;

    void Awake() => CacheCorners();

    void CacheCorners()
    {
        if (!cornerFL) cornerFL = transform.Find("CornerFL");
        if (!cornerFR) cornerFR = transform.Find("CornerFR");
        if (!cornerBL) cornerBL = transform.Find("CornerBL");
        if (!cornerBR) cornerBR = transform.Find("CornerBR");
    }

    public void ApplySize(Vector3 size)
    {
        transform.localScale = size;
        CacheCorners();
        if (!cornerFL || !cornerFR || !cornerBL || !cornerBR) return;

        float hx = size.x * 0.5f;
        float hy = size.y * 0.5f;
        float hz = size.z * 0.5f;

        cornerFL.localPosition = new Vector3(-hx, +hy, +hz);
        cornerFR.localPosition = new Vector3(+hx, +hy, +hz);
        cornerBL.localPosition = new Vector3(-hx, +hy, -hz);
        cornerBR.localPosition = new Vector3(+hx, +hy, -hz);
    }
}
