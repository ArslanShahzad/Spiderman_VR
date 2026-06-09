using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class GloveAnimationController : MonoBehaviour
{
    [Serializable]
    public class ButtonAnimEntry
    {
        [Tooltip("Just a label so you can tell entries apart in the Inspector")]
        public string label;

        [Tooltip("Bind any left controller button or trigger here")]
        public InputActionProperty action;

        [Tooltip("Animator Trigger parameter name to fire when button is pressed")]
        public string animationTrigger;
    }

    [Header("References")]
    public Animator animator;

    [Header("Button → Animation Mapping")]
    public List<ButtonAnimEntry> entries = new();

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
    }

    void Update()
    {
        foreach (var entry in entries)
        {
            if (entry.action.action == null) continue;
            if (string.IsNullOrEmpty(entry.animationTrigger)) continue;

            if (entry.action.action.WasPressedThisFrame())
                animator.Play(entry.animationTrigger);
        }
    }
}
