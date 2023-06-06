#if !UNITY_IOS
/*===============================================================================
Copyright (C) 2023 Immersal - Part of Hexagon. All Rights Reserved.

This file is part of the Immersal SDK.

The Immersal SDK cannot be copied, distributed, or made available to
third-parties for commercial purposes without written permission of Immersal Ltd.

Contact sdk@immersal.com for licensing requests.
===============================================================================*/

using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine.XR.ARFoundation;
using Immersal.REST;
using Immersal.AR;

namespace Immersal.XR.MagicLeap
{
	[RequireComponent(typeof(ICameraDataProvider))]
    public class MLLocalizer : LocalizerBase
    {
	    private ICameraDataProvider m_CameraDataProvider;

	    [SerializeField] private RawImage imageRenderer;
	    [SerializeField] private bool saveLocalizationImageOnDevice = false;

	    private Texture2D textureBuffer;
	    
		private static MLLocalizer instance = null;
	    	    
        private void ARSessionStateChanged(ARSessionStateChangedEventArgs args)
        {
            CheckTrackingState(args.state);
        }

        private void CheckTrackingState(ARSessionState newState)
        {
            isTracking = newState == ARSessionState.SessionTracking;

            if (!isTracking)
            {
                foreach (KeyValuePair<Transform, SpaceContainer> item in ARSpace.transformToSpace)
                    item.Value.filter.InvalidateHistory();
            }
        }

		public static MLLocalizer Instance
		{
			get
			{
#if UNITY_EDITOR
				if (instance == null && !Application.isPlaying)
				{
					instance = UnityEngine.Object.FindObjectOfType<MLLocalizer>();
				}
#endif
				if (instance == null)
				{
					Debug.LogError("No MLLocalizer instance found. Ensure one exists in the scene.");
				}
				return instance;
			}
		}

		void Awake()
		{
			if (instance == null)
			{
				instance = this;
			}
			if (instance != this)
			{
				Debug.LogError("There must be only one MLLocalizer object in a scene.");
				UnityEngine.Object.DestroyImmediate(this);
				return;
			}
		}

        public override void OnEnable()
        {
			base.OnEnable();
#if !UNITY_EDITOR
			CheckTrackingState(ARSession.state);
			ARSession.stateChanged += ARSessionStateChanged;
#endif
        }

        public override void OnDisable()
        {
#if !UNITY_EDITOR
			ARSession.stateChanged -= ARSessionStateChanged;
#endif
            base.OnDisable();
        }

        public override void Start()
        {
	        base.Start();
			m_Sdk.RegisterLocalizer(instance);

			//Immersal.Core.SetInteger("NumThreads", 1);
			//Immersal.Core.SetInteger("ImageCompressionLevel", 0);
	        
	        if (m_CameraDataProvider == null)
	        {
		        m_CameraDataProvider = GetComponent<ICameraDataProvider>();
		        if (m_CameraDataProvider == null)
		        {
			        Debug.LogError("Could not find Camera Data Provider.");
			        enabled = false;
		        }
	        }
        }

        public override async void Localize()
        {
            if (m_CameraDataProvider.TryAcquireLatestData(out CaptureData data))
            {
                stats.localizationAttemptCount++;

                Vector3 camPos = data.CameraTransform.position;
                Quaternion camRot = data.CameraTransform.rotation;
                Vector4 intrinsics = data.Intrinsics;

                Vector3 pos = Vector3.zero;
                Quaternion rot = Quaternion.identity;

                float startTime = Time.realtimeSinceStartup;

                Task<int> t = Task.Run(() =>
                {
                    return Immersal.Core.LocalizeImage(out pos, out rot, data.Width, data.Height, ref intrinsics, data.PixelBuffer);
                });

                await t;

                int mapHandle = t.Result;
                int mapId = ARMap.MapHandleToId(mapHandle);
                float elapsedTime = Time.realtimeSinceStartup - startTime;

                if (mapId > 0 && ARSpace.mapIdToMap.ContainsKey(mapId))
                {
                    rot *= Quaternion.Euler(0f, 0f, 180.0f);
                    pos = ARHelper.SwitchHandedness(pos);
                    rot = ARHelper.SwitchHandedness(rot);

                    LocalizerDebugLog(string.Format("Relocalized in {0} seconds", elapsedTime));

                    stats.localizationSuccessCount++;

                    ARMap map = ARSpace.mapIdToMap[mapId];

                    if (mapId != lastLocalizedMapId)
                    {
                        if (resetOnMapChange)
                        {
                            Reset();
                        }

                        lastLocalizedMapId = mapId;

                        OnMapChanged?.Invoke(mapId);
                    }

                    MapOffset mo = ARSpace.mapIdToOffset[mapId];
                    Matrix4x4 offsetNoScale = Matrix4x4.TRS(mo.position, mo.rotation, Vector3.one);
                    Vector3 scaledPos = Vector3.Scale(pos, mo.scale);
                    Matrix4x4 cloudSpace = offsetNoScale * Matrix4x4.TRS(scaledPos, rot, Vector3.one);
                    Matrix4x4 trackerSpace = Matrix4x4.TRS(camPos, camRot, Vector3.one);
                    Matrix4x4 m = trackerSpace * (cloudSpace.inverse);

                    if (useFiltering)
                        mo.space.filter.RefinePose(m);
                    else
                        ARSpace.UpdateSpace(mo.space, m.GetColumn(3), m.rotation);

                    GetLocalizerPose(out lastLocalizedPose, mapId, pos, rot, m.inverse);
                    OnPoseFound?.Invoke(lastLocalizedPose);
                    map.NotifySuccessfulLocalization(mapId);
                }
                else
                {
                    LocalizerDebugLog(string.Format("Localization attempt failed after {0} seconds", elapsedTime));
                }
            }

            base.Localize();
        }

        public override async void LocalizeServer(SDKMapId[] mapIds)
        {
            byte[] pngBytes = null;
            CaptureData data = default;

            Task<bool> t = Task.Run(() =>
            {
                return m_CameraDataProvider.TryAcquirePngBytes(out pngBytes, out data);
            });

            await t;

            if (t.Result)
            {
                stats.localizationAttemptCount++;
                
                JobLocalizeServerAsync j = new JobLocalizeServerAsync();
                
                Vector3 camPos = data.CameraTransform.position;
                Quaternion camRot = data.CameraTransform.rotation;
                Vector4 intrinsics = data.Intrinsics;

                j.mapIds = mapIds;
                j.intrinsics = intrinsics;
                j.image = pngBytes;

                RenderPreview(pngBytes);
                
                float startTime = 0f;
                
                j.OnStart += () =>
                {
                    startTime = Time.realtimeSinceStartup;
                };

                j.OnResult += (SDKLocalizeResult result) =>
                {
                    float elapsedTime = Time.realtimeSinceStartup - startTime;

                    if (result.success)
                    {
                        LocalizerDebugLog("*************************** On-Server Localization Success ***************************");
                        LocalizerDebugLog(string.Format("Relocalized in {0} seconds", elapsedTime));
                        
                        int mapId = result.map;
                        
                        if (mapId > 0 && ARSpace.mapIdToOffset.ContainsKey(mapId))
                        {
                            ARMap map = ARSpace.mapIdToMap[mapId];

                            if (mapId != lastLocalizedMapId)
                            {
                                if (resetOnMapChange)
                                {
                                    Reset();
                                }
                                
                                lastLocalizedMapId = mapId;
                                OnMapChanged?.Invoke(mapId);
                            }

                            MapOffset mo = ARSpace.mapIdToOffset[mapId];
                            stats.localizationSuccessCount++;
                            
                            // Response matrix from server
                            Matrix4x4 responseMatrix = Matrix4x4.identity;
                            responseMatrix.m00 = result.r00; responseMatrix.m01 = result.r01; responseMatrix.m02 = result.r02; responseMatrix.m03 = result.px;
                            responseMatrix.m10 = result.r10; responseMatrix.m11 = result.r11; responseMatrix.m12 = result.r12; responseMatrix.m13 = result.py;
                            responseMatrix.m20 = result.r20; responseMatrix.m21 = result.r21; responseMatrix.m22 = result.r22; responseMatrix.m23 = result.pz;
                            
                            Vector3 pos = responseMatrix.GetColumn(3);
                            Quaternion rot = responseMatrix.rotation;
                            rot *= Quaternion.Euler(0f, 0f, 180f);
                            pos = ARHelper.SwitchHandedness(pos);
                            rot = ARHelper.SwitchHandedness(rot);
                            
                            Matrix4x4 offsetNoScale = Matrix4x4.TRS(mo.position, mo.rotation, Vector3.one);
                            Vector3 scaledPos = Vector3.Scale(pos, mo.scale);
                            Matrix4x4 cloudSpace = offsetNoScale * Matrix4x4.TRS(scaledPos, rot, Vector3.one);
                            Matrix4x4 trackerSpace = Matrix4x4.TRS(camPos, camRot, Vector3.one);
                            Matrix4x4 m = trackerSpace * (cloudSpace.inverse);

                            if (useFiltering)
                                mo.space.filter.RefinePose(m);
                            else
                                ARSpace.UpdateSpace(mo.space, m.GetColumn(3), m.rotation);
                            
                            double[] ecef = map.MapToEcefGet();
                            LocalizerBase.GetLocalizerPose(out lastLocalizedPose, mapId, pos, rot, m.inverse, ecef);
                            map.NotifySuccessfulLocalization(mapId);
                            OnPoseFound?.Invoke(lastLocalizedPose);
                        }
                    }
                    else
                    {
                        LocalizerDebugLog("*************************** On-Server Localization Failed ***************************");
                        LocalizerDebugLog(string.Format("Localization attempt failed after {0} seconds", elapsedTime));
                    }
                };

                await j.RunJobAsync();
            }

			base.LocalizeServer(mapIds);
		}

        public override async void LocalizeGeoPose(SDKMapId[] mapIds)
        {
            byte[] pngBytes = null;
            CaptureData data = default;

            Task<bool> t = Task.Run(() =>
            {
                return m_CameraDataProvider.TryAcquirePngBytes(out pngBytes, out data);
            });

            await t;

            if (t.Result)
            {
                stats.localizationAttemptCount++;

                JobGeoPoseAsync j = new JobGeoPoseAsync();

                Vector3 camPos = data.CameraTransform.position;
                Quaternion camRot = data.CameraTransform.rotation;
                Vector4 intrinsics = data.Intrinsics;

                j.mapIds = mapIds;
                j.intrinsics = intrinsics;
                j.image = pngBytes;

                RenderPreview(pngBytes);

                float startTime = 0f;

                j.OnStart += () =>
                {
                    startTime = Time.realtimeSinceStartup;
                };

                j.OnResult += (SDKGeoPoseResult result) =>
                {
                    float elapsedTime = Time.realtimeSinceStartup - startTime;

                    if (result.success)
                    {
                        LocalizerDebugLog("*************************** GeoPose Localization Succeeded ***************************");
                        LocalizerDebugLog(string.Format("Relocalized in {0} seconds", elapsedTime));

                        int mapId = result.map;
                        double latitude = result.latitude;
                        double longitude = result.longitude;
                        double ellipsoidHeight = result.ellipsoidHeight;
                        Quaternion rot = new Quaternion(result.quaternion[1], result.quaternion[2], result.quaternion[3], result.quaternion[0]);
                        LocalizerDebugLog(string.Format("GeoPose returned latitude: {0}, longitude: {1}, ellipsoidHeight: {2}, quaternion: {3}", latitude, longitude, ellipsoidHeight, rot));

                        double[] ecef = new double[3];
                        double[] wgs84 = new double[3] { latitude, longitude, ellipsoidHeight };
                        Core.PosWgs84ToEcef(ecef, wgs84);

                        if (ARSpace.mapIdToMap.ContainsKey(mapId))
                        {
                            ARMap map = ARSpace.mapIdToMap[mapId];

                            if (mapId != lastLocalizedMapId)
                            {
                                if (resetOnMapChange)
                                {
                                    Reset();
                                }

                                lastLocalizedMapId = mapId;
                                OnMapChanged?.Invoke(mapId);
                            }

                            MapOffset mo = ARSpace.mapIdToOffset[mapId];
                            stats.localizationSuccessCount++;

                            double[] mapToEcef = map.MapToEcefGet();
                            Vector3 mapPos;
                            Quaternion mapRot;
                            Core.PosEcefToMap(out mapPos, ecef, mapToEcef);
                            Core.RotEcefToMap(out mapRot, rot, mapToEcef);

                            mapRot *= Quaternion.Euler(0f, 0f, 180.0f);
                            mapPos = ARHelper.SwitchHandedness(mapPos);
                            mapRot = ARHelper.SwitchHandedness(mapRot);

                            Matrix4x4 offsetNoScale = Matrix4x4.TRS(mo.position, mo.rotation, Vector3.one);
                            Vector3 scaledPos = Vector3.Scale(mapPos, mo.scale);
                            Matrix4x4 cloudSpace = offsetNoScale * Matrix4x4.TRS(scaledPos, mapRot, Vector3.one);
                            Matrix4x4 trackerSpace = Matrix4x4.TRS(camPos, camRot, Vector3.one);
                            Matrix4x4 m = trackerSpace * (cloudSpace.inverse);

                            if (useFiltering)
                                mo.space.filter.RefinePose(m);
                            else
                                ARSpace.UpdateSpace(mo.space, m.GetColumn(3), m.rotation);

                            LocalizerBase.GetLocalizerPose(out lastLocalizedPose, mapId, cloudSpace.GetColumn(3), cloudSpace.rotation, m.inverse, mapToEcef);
                            map.NotifySuccessfulLocalization(mapId);
                            OnPoseFound?.Invoke(lastLocalizedPose);
                        }
                    }
                    else
                    {
                        LocalizerDebugLog("*************************** GeoPose Localization Failed ***************************");
                        LocalizerDebugLog(string.Format("GeoPose localization attempt failed after {0} seconds", elapsedTime));
                    }
                };

                await j.RunJobAsync();
            }

            base.LocalizeGeoPose(mapIds);
        }

        private void RenderPreview(byte[] pngBytes)
        {
            if (imageRenderer && imageRenderer.enabled)
            {
                if (!textureBuffer)
                {
                    textureBuffer = new Texture2D(8,8);
                    textureBuffer.filterMode = FilterMode.Point;
                }

                bool loadSuccessful = textureBuffer.LoadImage(pngBytes);
                if (loadSuccessful && textureBuffer.width != 8 && textureBuffer.height != 8)
                {
                    imageRenderer.texture = textureBuffer;
                }
            }

            if (saveLocalizationImageOnDevice)
            {
                File.WriteAllBytes(Path.Combine(Application.persistentDataPath, "latestImage.png"), pngBytes);
            }
        }
    }
}
#endif