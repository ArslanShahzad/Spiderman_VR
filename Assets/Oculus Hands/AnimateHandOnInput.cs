using System.Collections;
using System.Collections.Generic;
using UnityEngine;
//using enity input system
using UnityEngine.InputSystem;

public class AnimateHandOnInput : MonoBehaviour
{
    public InputActionProperty pinchAnimationAction;
    public InputActionProperty gripAnimationAction;
    public Animator pinchAnimation;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        float triggerValue = pinchAnimationAction.action.ReadValue<float>();
        pinchAnimation.SetFloat("Trigger", triggerValue);

        float gripValue = gripAnimationAction.action.ReadValue<float>();
        pinchAnimation.SetFloat("Grip", gripValue);
        //Debug.Log("Trigger Value: " + triggerValue);
    }
}
