using UnityEngine;

public class PhysicRig : MonoBehaviour
{
    public Transform playerHead;
    public CapsuleCollider BodyCollider;

    public float heightOffset = 0.5f;
    public float Heightmax = 2.0f;
    public float Heightmin = 0.5f;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void FixedUpdate()
    {
        BodyCollider.height = Mathf.Clamp(playerHead.localPosition.y + heightOffset, Heightmin, Heightmax);
        BodyCollider.center = new Vector3(playerHead.localPosition.x, BodyCollider.height / 2, playerHead.localPosition.z);
    }
}
