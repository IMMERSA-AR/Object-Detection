using UnityEngine;
using System.Collections;

public class SH_PortalController : MonoBehaviour
{
    public float openSpeed = 2f;
    public Vector3 targetScale = new Vector3(1.25f, 1.25f, 1f);

    void Start()
    {
        // Start the portal at zero size so it's "closed"
        transform.localScale = Vector3.zero;
        StartCoroutine(OpenPortal());
    }

    IEnumerator OpenPortal()
    {
        float timer = 0;
        while (timer < 1f)
        {
            timer += Time.deltaTime * openSpeed;
            // Smoothly grows the portal from 0 to its full size
            transform.localScale = Vector3.Lerp(Vector3.zero, targetScale, timer);
            yield return null;
        }
    }
}