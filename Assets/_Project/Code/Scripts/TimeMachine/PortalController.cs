using UnityEngine;
using System.Collections;

public class SH_PortalController : MonoBehaviour
{
    public float openSpeed = 2f;
    public Vector3 targetScale = new Vector3(25f, 25f, 1f);

    void Start()
    {
        transform.localScale = Vector3.zero;
        StartCoroutine(OpenPortal());
    }

    IEnumerator OpenPortal()
    {
        float timer = 0;
        while (timer < 1f)
        {
            timer += Time.deltaTime * openSpeed;
            transform.localScale = Vector3.Lerp(Vector3.zero, targetScale, timer);
            yield return null;
        }
    }
}