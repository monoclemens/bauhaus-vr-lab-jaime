using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using VRSYS.Core.Logging;


public class VirtualHand : MonoBehaviour
{
    #region Member Variables

    private enum VirtualHandsMethod 
    {
        Hitting,
        Reparenting,
        Calculation
    }

    [Header("Input Actions")] 
    public InputActionProperty grabAction;
    public InputActionProperty toggleAction;

    [Header("Configuration")]
    [SerializeField] private VirtualHandsMethod grabMethod;
    public HandCollider handCollider;
    
    // calculation variables
    private GameObject grabbedObject;
    private MadPads_Pad playedPad;
    private Matrix4x4 offsetMatrix;
    private NetworkedAudioPlayer initialAudioPlayer;

    private bool canGrab
    {
        get
        {
            if (handCollider.isColliding)
                return handCollider.collidingObject.GetComponent<ManipulationSelector>().RequestGrab();
            return false;
        }
    }

    #endregion

    #region MonoBehaviour Callbacks

    private void Start()
    {
        if(GetComponentInParent<NetworkObject>() != null)
            if (!GetComponentInParent<NetworkObject>().IsOwner)
            {
                Destroy(this);
                return;
            }
        //initialAudioPlayer = GameObject.Find("InteractableCube").GetComponent<NetworkedAudioPlayer>();
        //initialAudioPlayer.SetAudio("initial_seq");
    }

    private void Update()
    {
        if (toggleAction.action.WasPressedThisFrame())
        {
            grabMethod = (VirtualHandsMethod)(((int)grabMethod + 1) % 3);
        }
        
        switch (grabMethod)
        {
            case VirtualHandsMethod.Hitting:
                Hitting();
                break;
            case VirtualHandsMethod.Reparenting:
                ReparentingGrab();
                break;
            case VirtualHandsMethod.Calculation:
                CalculationGrab();
                break;
        }
    }

    #endregion

    #region Grab Methods

    private void Hitting()
    {
        if (grabAction.action.WasPressedThisFrame() && handCollider.isColliding)
        {
            playedPad = handCollider.collidingObject.GetComponent<MadPads_Pad>();
            playedPad.Sync();
            playedPad.Play(NetworkManager.Singleton.LocalClientId);
        }
        else if(grabAction.action.WasPressedThisFrame())
        {
            ExtendedLogger.LogInfo(GetType().Name, "girdik");
            //there should be a game manager
        }
        
        /*else if (grabAction.action.WasReleasedThisFrame())
        {
            if(grabbedObject != null)
                grabbedObject.GetComponent<ManipulationSelector>().Release();
            grabbedObject = null;
        }*/
    }

    private void ReparentingGrab()
    {
        if (grabAction.action.WasPressedThisFrame())
        {
            if (grabbedObject == null && handCollider.isColliding && canGrab)
            {
                grabbedObject = handCollider.collidingObject;
                grabbedObject.transform.SetParent(transform, true);
            }
        }
        else if(grabAction.action.WasReleasedThisFrame())
        {
            if (grabbedObject != null)
            {
                grabbedObject.GetComponent<ManipulationSelector>().Release();
                grabbedObject.transform.SetParent(null, true);
            }
            
            grabbedObject = null;
        }
    }

    private void CalculationGrab()
    {
        if (grabAction.action.WasPressedThisFrame())
        {
            if (grabbedObject == null && handCollider.isColliding && canGrab)
            {
                grabbedObject = handCollider.collidingObject;
                offsetMatrix = GetTransformationMatrix(transform, true).inverse *
                               GetTransformationMatrix(grabbedObject.transform, true);
            }
        }
        else if (grabAction.action.IsPressed())
        {
            if (grabbedObject != null)
            {
                Matrix4x4 newTransform = GetTransformationMatrix(transform, true) * offsetMatrix;

                grabbedObject.transform.position = newTransform.GetColumn(3);
                grabbedObject.transform.rotation = newTransform.rotation;
            }
        }
        else if (grabAction.action.WasReleasedThisFrame())
        {
            if(grabbedObject != null)
                grabbedObject.GetComponent<ManipulationSelector>().Release();
            grabbedObject = null;
            offsetMatrix = Matrix4x4.identity;
        }
    }

    #endregion

    #region Utility Functions

    public Matrix4x4 GetTransformationMatrix(Transform t, bool inWorldSpace = true)
    {
        if (inWorldSpace)
        {
            return Matrix4x4.TRS(t.position, t.rotation, t.lossyScale);
        }
        else
        {
            return Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
        }
    }

    #endregion
}
