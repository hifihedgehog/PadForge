using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>
    /// The action set that reads both motion controllers (issue #403).
    ///
    /// <para>A headset needs no actions at all, which is why it shipped
    /// first. A controller needs the whole apparatus: an action set, an
    /// action per control, a suggested binding for every interaction profile
    /// worth supporting, the set attached before the session begins, and a
    /// synchronization every sample.</para>
    ///
    /// <para>Bindings are suggested once, before attaching. Suggesting again
    /// afterward is rejected, so this is not a way to discover a controller
    /// that appears later. Supporting a controller means having its profile
    /// in the table below from the start.</para>
    /// </summary>
    internal sealed unsafe class OpenXrActions : IDisposable
    {
        private readonly Action<string> _log;
        private PFN_xrGetActionStatePose _getPose;
        private PFN_xrGetActionStateFloat _getFloat;
        private PFN_xrGetActionStateVector2f _getVector2f;
        private PFN_xrGetActionStateBoolean _getBoolean;
        private PFN_xrSyncActions _sync;
        private PFN_xrLocateSpace _locate;
        private PFN_xrDestroyActionSet _destroyActionSet;
        private PFN_xrDestroySpace _destroySpace;

        private ulong _session;
        private ulong _actionSet;
        private readonly ulong[] _handPath = new ulong[2];
        private readonly ulong[] _gripSpace = new ulong[2];

        private ulong _gripPose, _trigger, _squeeze, _stick, _stickClick;
        private ulong _primary, _secondary, _menu;

        public bool Ready { get; private set; }

        public OpenXrActions(Action<string> log) => _log = log ?? (_ => { });

        /// <summary>The profiles this reads. Each lists the binding path for
        /// every action it supports, and a null means that profile has no
        /// such control, which is different from a control that reads
        /// zero.</summary>
        private static readonly (string Profile, string[] Left, string[] Right)[] Profiles =
        {
            // Oculus Touch, which is what a Quest reports through Virtual
            // Desktop and through SteamVR alike.
            ("/interaction_profiles/oculus/touch_controller",
             new[] { "/user/hand/left/input/grip/pose", "/user/hand/left/input/trigger/value",
                     "/user/hand/left/input/squeeze/value", "/user/hand/left/input/thumbstick",
                     "/user/hand/left/input/thumbstick/click", "/user/hand/left/input/x/click",
                     "/user/hand/left/input/y/click", "/user/hand/left/input/menu/click" },
             new[] { "/user/hand/right/input/grip/pose", "/user/hand/right/input/trigger/value",
                     "/user/hand/right/input/squeeze/value", "/user/hand/right/input/thumbstick",
                     "/user/hand/right/input/thumbstick/click", "/user/hand/right/input/a/click",
                     "/user/hand/right/input/b/click", null }),

            // Valve Index.
            ("/interaction_profiles/valve/index_controller",
             new[] { "/user/hand/left/input/grip/pose", "/user/hand/left/input/trigger/value",
                     "/user/hand/left/input/squeeze/value", "/user/hand/left/input/thumbstick",
                     "/user/hand/left/input/thumbstick/click", "/user/hand/left/input/a/click",
                     "/user/hand/left/input/b/click", "/user/hand/left/input/system/click" },
             new[] { "/user/hand/right/input/grip/pose", "/user/hand/right/input/trigger/value",
                     "/user/hand/right/input/squeeze/value", "/user/hand/right/input/thumbstick",
                     "/user/hand/right/input/thumbstick/click", "/user/hand/right/input/a/click",
                     "/user/hand/right/input/b/click", "/user/hand/right/input/system/click" }),

            // The Khronos simple controller, the generic fallback profile. It
            // carries a pose, one button and a menu button, so it keeps a pose
            // usable on hardware none of the profiles above describe.
            ("/interaction_profiles/khr/simple_controller",
             new[] { "/user/hand/left/input/grip/pose", null, null, null, null,
                     "/user/hand/left/input/select/click", null,
                     "/user/hand/left/input/menu/click" },
             new[] { "/user/hand/right/input/grip/pose", null, null, null, null,
                     "/user/hand/right/input/select/click", null,
                     "/user/hand/right/input/menu/click" }),
        };

        /// <summary>
        /// Builds the action set and suggests every profile's bindings.
        /// Returns false without throwing when the runtime declines, because
        /// a runtime with no controllers is an ordinary case and the headset
        /// must keep working.
        /// </summary>
        public bool TryCreate(ulong instance, ulong session, Func<string, Delegate> resolve)
        {
            _session = session;
            var createActionSet = (PFN_xrCreateActionSet)resolve("xrCreateActionSet");
            var createAction = (PFN_xrCreateAction)resolve("xrCreateAction");
            var stringToPath = (PFN_xrStringToPath)resolve("xrStringToPath");
            var suggest = (PFN_xrSuggestInteractionProfileBindings)
                resolve("xrSuggestInteractionProfileBindings");
            var attach = (PFN_xrAttachSessionActionSets)resolve("xrAttachSessionActionSets");
            var createActionSpace = (PFN_xrCreateActionSpace)resolve("xrCreateActionSpace");
            _getPose = (PFN_xrGetActionStatePose)resolve("xrGetActionStatePose");
            _getFloat = (PFN_xrGetActionStateFloat)resolve("xrGetActionStateFloat");
            _getVector2f = (PFN_xrGetActionStateVector2f)resolve("xrGetActionStateVector2f");
            _getBoolean = (PFN_xrGetActionStateBoolean)resolve("xrGetActionStateBoolean");
            _sync = (PFN_xrSyncActions)resolve("xrSyncActions");
            _locate = (PFN_xrLocateSpace)resolve("xrLocateSpace");
            _destroyActionSet = (PFN_xrDestroyActionSet)resolve("xrDestroyActionSet");
            _destroySpace = (PFN_xrDestroySpace)resolve("xrDestroySpace");

            // Require exactly the entry points this actually calls. An
            // earlier version required xrGetActionStatePose, which nothing
            // here invokes (pose validity comes from xrLocateSpace's flags),
            // and did not require the state readers it does invoke. The
            // vector2 reader then had no case in OpenXrSession.ResolveRaw,
            // and the thumbsticks sat at center on every runtime.
            if (createActionSet == null || createAction == null || stringToPath == null
                || suggest == null || attach == null || createActionSpace == null
                || _getFloat == null || _getBoolean == null || _getVector2f == null
                || _sync == null || _locate == null)
                return false;

            var setInfo = new XrActionSetCreateInfo { type = XR_TYPE_ACTION_SET_CREATE_INFO, priority = 0 };
            WriteFixedUtf8(setInfo.actionSetName, XR_MAX_ACTION_SET_NAME_SIZE, "padforge_hands");
            WriteFixedUtf8(setInfo.localizedActionSetName, XR_MAX_LOCALIZED_ACTION_SET_NAME_SIZE,
                           "PadForge Controllers");
            if (createActionSet(instance, ref setInfo, out _actionSet) != XR_SUCCESS) return false;

            if (stringToPath(instance, "/user/hand/left", out _handPath[0]) != XR_SUCCESS
                || stringToPath(instance, "/user/hand/right", out _handPath[1]) != XR_SUCCESS)
                return false;

            // One action per control, each with both hands as subaction paths,
            // so a single action answers for left and right separately.
            if (!Make(createAction, "grip_pose", "Grip Pose", XR_ACTION_TYPE_POSE_INPUT, out _gripPose)
                || !Make(createAction, "trigger", "Trigger", XR_ACTION_TYPE_FLOAT_INPUT, out _trigger)
                || !Make(createAction, "squeeze", "Grip", XR_ACTION_TYPE_FLOAT_INPUT, out _squeeze)
                || !Make(createAction, "thumbstick", "Thumbstick", XR_ACTION_TYPE_VECTOR2F_INPUT, out _stick)
                || !Make(createAction, "stick_click", "Thumbstick Click", XR_ACTION_TYPE_BOOLEAN_INPUT, out _stickClick)
                || !Make(createAction, "primary", "Primary Button", XR_ACTION_TYPE_BOOLEAN_INPUT, out _primary)
                || !Make(createAction, "secondary", "Secondary Button", XR_ACTION_TYPE_BOOLEAN_INPUT, out _secondary)
                || !Make(createAction, "menu", "Menu Button", XR_ACTION_TYPE_BOOLEAN_INPUT, out _menu))
                return false;

            var actions = new[] { _gripPose, _trigger, _squeeze, _stick, _stickClick,
                                  _primary, _secondary, _menu };

            int suggested = 0;
            foreach (var (profile, left, right) in Profiles)
            {
                if (stringToPath(instance, profile, out ulong profilePath) != XR_SUCCESS) continue;
                var bindings = new List<XrActionSuggestedBinding>();
                for (int i = 0; i < actions.Length; i++)
                {
                    foreach (string path in new[] { left[i], right[i] })
                    {
                        if (path == null) continue;
                        if (stringToPath(instance, path, out ulong bound) != XR_SUCCESS) continue;
                        bindings.Add(new XrActionSuggestedBinding { action = actions[i], binding = bound });
                    }
                }
                if (bindings.Count == 0) continue;

                var array = Marshal.AllocHGlobal(sizeof(XrActionSuggestedBinding) * bindings.Count);
                try
                {
                    for (int i = 0; i < bindings.Count; i++)
                        ((XrActionSuggestedBinding*)array)[i] = bindings[i];
                    var info = new XrInteractionProfileSuggestedBinding
                    {
                        type = XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING,
                        interactionProfile = profilePath,
                        countSuggestedBindings = (uint)bindings.Count,
                        suggestedBindings = array,
                    };
                    // A runtime rejecting one profile is normal. It only has
                    // to accept the ones its hardware uses.
                    if (suggest(instance, ref info) == XR_SUCCESS) suggested++;
                }
                finally { Marshal.FreeHGlobal(array); }
            }
            if (suggested == 0)
            {
                _log("OpenXR: no controller profile was accepted, hands stay offline");
                return false;
            }

            IntPtr sets = Marshal.AllocHGlobal(sizeof(ulong));
            try
            {
                Marshal.WriteInt64(sets, (long)_actionSet);
                var attachInfo = new XrSessionActionSetsAttachInfo
                {
                    type = XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO,
                    countActionSets = 1,
                    actionSets = sets,
                };
                if (attach(session, ref attachInfo) != XR_SUCCESS)
                {
                    _log("OpenXR: the runtime refused the controller action set");
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(sets); }

            for (int hand = 0; hand < 2; hand++)
            {
                var spaceInfo = new XrActionSpaceCreateInfo
                {
                    type = XR_TYPE_ACTION_SPACE_CREATE_INFO,
                    action = _gripPose,
                    subactionPath = _handPath[hand],
                };
                spaceInfo.poseInActionSpace.orientation.w = 1f;
                if (createActionSpace(session, ref spaceInfo, out _gripSpace[hand]) != XR_SUCCESS)
                    _gripSpace[hand] = 0;
            }

            Ready = true;
            _log($"OpenXR: controllers ready, {suggested} interaction profile(s) accepted");
            return true;

            bool Make(PFN_xrCreateAction create, string name, string localized, int type, out ulong action)
            {
                action = 0;
                IntPtr paths = Marshal.AllocHGlobal(sizeof(ulong) * 2);
                try
                {
                    ((ulong*)paths)[0] = _handPath[0];
                    ((ulong*)paths)[1] = _handPath[1];
                    var info = new XrActionCreateInfo
                    {
                        type = XR_TYPE_ACTION_CREATE_INFO,
                        actionType = type,
                        countSubactionPaths = 2,
                        subactionPaths = paths,
                    };
                    WriteFixedUtf8(info.actionName, XR_MAX_ACTION_NAME_SIZE, name);
                    WriteFixedUtf8(info.localizedActionName, XR_MAX_LOCALIZED_ACTION_NAME_SIZE, localized);
                    return create(_actionSet, ref info, out action) == XR_SUCCESS;
                }
                finally { Marshal.FreeHGlobal(paths); }
            }
        }

        private IntPtr _activeSets;

        /// <summary>
        /// Synchronizes the action set. False means the controls are not live
        /// this sample, which is the ordinary answer while a game holds focus.
        /// </summary>
        public bool Sync()
        {
            if (!Ready) return false;
            if (_activeSets == IntPtr.Zero)
            {
                _activeSets = Marshal.AllocHGlobal(sizeof(XrActiveActionSet));
                ((XrActiveActionSet*)_activeSets)[0] =
                    new XrActiveActionSet { actionSet = _actionSet, subactionPath = 0 };
            }
            var info = new XrActionsSyncInfo
            {
                type = XR_TYPE_ACTIONS_SYNC_INFO,
                countActiveActionSets = 1,
                activeActionSets = _activeSets,
            };
            // XR_SESSION_NOT_FOCUSED is a success code and means the controls
            // are not ours to read right now. Treating any non-negative result
            // as live would publish stale input.
            return _sync(_session, ref info) == XR_SUCCESS;
        }

        /// <summary>Reads one hand against the base space.</summary>
        public OpenXrHandState Read(OpenXrHand hand, ulong baseSpace, long time,
                                    bool controlsLive, ref OpenXrHeadPose.Baseline baseline)
        {
            var state = OpenXrHandState.Empty;
            if (!Ready) return state;
            int index = (int)hand;
            ulong sub = _handPath[index];

            if (_gripSpace[index] != 0 && time != 0)
            {
                var location = new XrSpaceLocation { type = XR_TYPE_SPACE_LOCATION };
                const ulong needed = XR_SPACE_LOCATION_ORIENTATION_VALID_BIT
                                   | XR_SPACE_LOCATION_POSITION_VALID_BIT
                                   | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT
                                   | XR_SPACE_LOCATION_POSITION_TRACKED_BIT;
                if (_locate(_gripSpace[index], baseSpace, time, ref location) == XR_SUCCESS
                    && (location.locationFlags & needed) == needed)
                {
                    Span<double> pose = stackalloc double[HeadPose.PoseCount];
                    var p = location.pose.position;
                    var o = location.pose.orientation;
                    if (OpenXrHeadPose.TryFillPose(p.x, p.y, p.z, o.x, o.y, o.z, o.w,
                                                   ref baseline, pose))
                    {
                        state.PoseValid = true;
                        state.TX = pose[HeadPose.TX];
                        state.TY = pose[HeadPose.TY];
                        state.TZ = pose[HeadPose.TZ];
                        state.YawDeg = pose[HeadPose.Yaw];
                        state.PitchDeg = pose[HeadPose.Pitch];
                        state.RollDeg = pose[HeadPose.Roll];
                    }
                }
            }

            if (!controlsLive) return state;
            state.ControlsActive = true;
            state.Trigger = Float(_trigger, sub);
            state.Squeeze = Float(_squeeze, sub);
            // The thumbstick is a Vector2f, not two floats. It was created
            // and bound from the first version and never read, which left
            // both stick axes resting at center on every runtime.
            Vector2(_stick, sub, out state.ThumbstickX, out state.ThumbstickY);
            state.ThumbstickClick = Bool(_stickClick, sub);
            state.PrimaryButton = Bool(_primary, sub);
            state.SecondaryButton = Bool(_secondary, sub);
            state.MenuButton = Bool(_menu, sub);
            return state;
        }

        private float Float(ulong action, ulong sub)
        {
            if (action == 0 || _getFloat == null) return 0f;
            var get = new XrActionStateGetInfo
            {
                type = XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = sub,
            };
            var state = new XrActionStateFloat { type = XR_TYPE_ACTION_STATE_FLOAT };
            if (_getFloat(_session, ref get, ref state) != XR_SUCCESS) return 0f;
            // An inactive action has no value. Its currentState is whatever
            // the struct was left holding.
            return state.isActive != 0 ? state.currentState : 0f;
        }

        /// <summary>A thumbstick's two axes. Zero for an inactive action,
        /// the same contract <see cref="Float"/> uses.</summary>
        private void Vector2(ulong action, ulong sub, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (action == 0 || _getVector2f == null) return;
            var get = new XrActionStateGetInfo
            {
                type = XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = sub,
            };
            var state = new XrActionStateVector2f { type = XR_TYPE_ACTION_STATE_VECTOR2F };
            if (_getVector2f(_session, ref get, ref state) != XR_SUCCESS) return;
            if (state.isActive == 0) return;
            x = state.currentState.x;
            y = state.currentState.y;
        }

        private bool Bool(ulong action, ulong sub)
        {
            if (action == 0 || _getBoolean == null) return false;
            var get = new XrActionStateGetInfo
            {
                type = XR_TYPE_ACTION_STATE_GET_INFO, action = action, subactionPath = sub,
            };
            var state = new XrActionStateBoolean { type = XR_TYPE_ACTION_STATE_BOOLEAN };
            if (_getBoolean(_session, ref get, ref state) != XR_SUCCESS) return false;
            return state.isActive != 0 && state.currentState != 0;
        }

        public void Dispose()
        {
            for (int i = 0; i < 2; i++)
            {
                try { if (_gripSpace[i] != 0) _destroySpace?.Invoke(_gripSpace[i]); } catch (Exception) { }
                _gripSpace[i] = 0;
            }
            try { if (_actionSet != 0) _destroyActionSet?.Invoke(_actionSet); } catch (Exception) { }
            _actionSet = 0;
            if (_activeSets != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_activeSets);
                _activeSets = IntPtr.Zero;
            }
            Ready = false;
        }
    }
}
