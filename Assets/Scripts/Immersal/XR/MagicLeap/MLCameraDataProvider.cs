#if !UNITY_IOS
/*===============================================================================
Copyright (C) 2023 Immersal - Part of Hexagon. All Rights Reserved.

This file is part of the Immersal SDK.

The Immersal SDK cannot be copied, distributed, or made available to
third-parties for commercial purposes without written permission of Immersal Ltd.

Contact sdk@immersal.com for licensing requests.
===============================================================================*/

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.MagicLeap;

namespace Immersal.XR.MagicLeap
{
    public class MLCameraDataProvider : MonoBehaviour, ICameraDataProvider
    {
        [SerializeField] private bool verboseDebugLogging;

        public bool IsCameraConnected => captureCamera != null && captureCamera.ConnectionEstablished;
        
        private GraphicsFormat pngFormat = GraphicsFormat.R8_UNorm;
        private Camera cam;
        private Transform cameraTransformAtLatestCapture;    
        private List<MLCamera.StreamCapability> streamCapabilities;
        private MLCamera captureCamera;
        private MLCamera.CameraOutput capturedFrameInfo;
        private MLCamera.PlaneInfo latestYUVPlane;
        private bool cameraDeviceAvailable;
        private bool isCapturingVideo = false;
        private MLCamera.CaptureType captureType = MLCamera.CaptureType.Video;
        private readonly MLPermissions.Callbacks permissionCallbacks = new MLPermissions.Callbacks();
        private MLCamera.IntrinsicCalibrationParameters? intrinsics;
        private byte[] pixelBuffer;

        private void Awake()
        {
            permissionCallbacks.OnPermissionGranted += OnPermissionGranted;
            permissionCallbacks.OnPermissionDenied += OnPermissionDenied;
            permissionCallbacks.OnPermissionDeniedAndDontAskAgain += OnPermissionDenied;

            isCapturingVideo = false;

            MLPermissions.RequestPermission(MLPermission.Camera, permissionCallbacks);

            cam = Camera.main;
        }

        private void OnDisable()
        {
            DisconnectCamera();
        }

        private bool TryAcquireIntrinsics(out Vector4 intr, out double[] dist)
        {
            intr = Vector4.zero;
            dist = new double[5];

            if (!(this.intrinsics is MLCamera.IntrinsicCalibrationParameters intrinsicsValue))
            {
                return false;
            }

            if (!isCapturingVideo) return false;

            //DisplayData(intrinsicsValue);

            intr.x = intrinsicsValue.FocalLength.x;
            intr.y = intrinsicsValue.FocalLength.y;
            intr.z = intrinsicsValue.PrincipalPoint.x;
            intr.w = intrinsicsValue.PrincipalPoint.y;
            dist = intrinsicsValue.Distortion;

            return true;
        }

        public bool TryAcquireLatestData(out CaptureData data)
        {
            data = default;

            if (latestYUVPlane.Data is null || latestYUVPlane.Data.Length <= 0) return false;
            if (!TryAcquireIntrinsics(out Vector4 intrinsics, out double[] distortion)) return false;

            GetUnpaddedBytes(latestYUVPlane, false, out byte[] processedImageData);

            if (processedImageData is null || processedImageData.Length <= 0) return false;

            unsafe
            {
                fixed (byte* pinnedData = processedImageData)
                {
                    data.PixelBuffer = (IntPtr)pinnedData;
                }
            }

            data.CameraTransform = cameraTransformAtLatestCapture;
            data.Intrinsics = intrinsics;
            data.Distortion = distortion;
            data.Width = (int)latestYUVPlane.Width;
            data.Height = (int)latestYUVPlane.Height;

            return data.PixelBuffer != IntPtr.Zero;
        }

        public bool TryAcquirePngBytes(out byte[] pngBytes, out CaptureData data)
        {
            data = default;
            pngBytes = null;

            if (latestYUVPlane.Data is null || latestYUVPlane.Data.Length <= 0) return false;
            if (!TryAcquireIntrinsics(out Vector4 intrinsics, out double[] distortion)) return false;

            data.CameraTransform = cameraTransformAtLatestCapture;
            data.Intrinsics = intrinsics;
            data.Distortion = distortion;
            data.Width = (int)latestYUVPlane.Width;
            data.Height = (int)latestYUVPlane.Height;

            GetUnpaddedBytes(latestYUVPlane, true, out byte[] grayBytes);

            const uint channel = 1;
            pngBytes = ImageConversion.EncodeArrayToPNG(grayBytes, pngFormat, latestYUVPlane.Width, latestYUVPlane.Height, latestYUVPlane.Width * channel);

            return !(pngBytes is null || pngBytes.Length <= 0);
        }

        private void GetUnpaddedBytes(MLCamera.PlaneInfo yBuffer, bool invertVertically, out byte[] outputBuffer)
        {
            byte[] data = yBuffer.Data;
            int width = (int)yBuffer.Width, height = (int)yBuffer.Height, size = width * height;
            int stride = invertVertically ? -(int)yBuffer.Stride : (int)yBuffer.Stride;
            int invertStartOffset = ((int)yBuffer.Stride * height) - (int)yBuffer.Stride;

            // use the same buffer internally
            if (pixelBuffer is null || pixelBuffer.Length != size)
            {
                pixelBuffer = new byte[size];
            }

            unsafe
            {
                fixed (byte* pinnedData = data, dstPtr = pixelBuffer)
                {
                    byte* srcPtr = invertVertically ? pinnedData + invertStartOffset : pinnedData;
                    if (width > 0 && height > 0) {
                        UnsafeUtility.MemCpyStride(dstPtr, width, srcPtr, stride, width, height);
                    }
                }
            }

            outputBuffer = pixelBuffer;
        }

        private void OnPermissionGranted(string permission)
        {
    #if UNITY_ANDROID
            MLPluginLog.Debug($"Granted {permission}.");
            TryEnableMLCamera();
    #endif
        }

        private void OnPermissionDenied(string permission)
        {
            if (permission == MLPermission.Camera)
            {
    #if UNITY_ANDROID
                MLPluginLog.Error($"{permission} denied, example won't function.");
    #endif
            }
        }

        private void TryEnableMLCamera()
        {
            if (!MLPermissions.CheckPermission(MLPermission.Camera).IsOk)
                return;

            StartCoroutine(EnableMLCamera());
        }

        /// <summary>
        /// Connects the MLCamera component and instantiates a new instance
        /// if it was never created.
        /// </summary>
        private IEnumerator EnableMLCamera()
        {
            while (!cameraDeviceAvailable)
            {
                MLResult result =
                    MLCamera.GetDeviceAvailabilityStatus(MLCamera.Identifier.CV, out cameraDeviceAvailable);
                if (!(result.IsOk && cameraDeviceAvailable))
                {
                    // Wait until camera device is available
                    yield return new WaitForSeconds(1.0f);
                }
            }

            Log("Camera device available");

            yield return new WaitForSeconds(1.0f);
            ConnectCamera();

            yield return new WaitForSeconds(1.0f);
            StartVideoCapture();
        }

        /// <summary>
        /// Connects to the MLCamera.
        /// </summary>
        private void ConnectCamera()
        {
            MLCamera.ConnectContext context = MLCamera.ConnectContext.Create();
            context.CamId = MLCamera.Identifier.CV;
            context.Flags = MLCamera.ConnectFlag.CamOnly;
            context.EnableVideoStabilization = true;

            captureCamera = MLCamera.CreateAndConnect(context);

            if (captureCamera != null)
            {
                if (GetImageStreamCapabilities())
                {
                    captureCamera.OnRawVideoFrameAvailable += OnCaptureRawVideoFrameAvailable;
                }
            }
        }

        /// <summary>
        /// Disconnects the camera.
        /// </summary>
        private void DisconnectCamera()
        {
            if (captureCamera == null || !IsCameraConnected)
                return;

            streamCapabilities = null;

            captureCamera.OnRawVideoFrameAvailable -= OnCaptureRawVideoFrameAvailable;
            captureCamera.Disconnect();
        }

        /// <summary>
        /// Captures a preview of the device's camera and displays it in front of the user.
        /// </summary>
        private void StartVideoCapture()
        {
            MLCamera.CaptureConfig captureConfig = new MLCamera.CaptureConfig();
            captureConfig.CaptureFrameRate = MLCamera.CaptureFrameRate._30FPS;
            captureConfig.StreamConfigs = new MLCamera.CaptureStreamConfig[1];
            captureConfig.StreamConfigs[0] =
                MLCamera.CaptureStreamConfig.Create(GetStreamCapability(), MLCamera.OutputFormat.YUV_420_888);

            MLResult result = captureCamera.PrepareCapture(captureConfig, out MLCamera.Metadata _);

            if (MLResult.DidNativeCallSucceed(result.Result, nameof(captureCamera.PrepareCapture)))
            {
                captureCamera.PreCaptureAEAWB();

                if (captureType == MLCamera.CaptureType.Video)
                {
                    result = captureCamera.CaptureVideoStart();
                    isCapturingVideo = MLResult.DidNativeCallSucceed(result.Result, nameof(captureCamera.CaptureVideoStart));
                }
            }
        }

        /// <summary>
        /// Gets currently selected StreamCapability
        /// </summary>
        private MLCamera.StreamCapability GetStreamCapability()
        {
            foreach (var streamCapability in streamCapabilities.Where(s => s.CaptureType == captureType))
            {
                Log(streamCapability.ToString());
                // option: 640x480, 1280x720, 1920x1080, 3840x2160
//                if (streamCapability.Width == 1920 && streamCapability.Height == 1080)
                if (streamCapability.Width == 1280 && streamCapability.Height == 960)
                {
                    return streamCapability;
                }
            }
            Log($"{streamCapabilities.ToString()} is selected!");
            return streamCapabilities[0];
        }

        /// <summary>
        /// Gets the Image stream capabilities.
        /// </summary>
        /// <returns>True if MLCamera returned at least one stream capability.</returns>
        private bool GetImageStreamCapabilities()
        {
            var result =
                captureCamera.GetStreamCapabilities(out MLCamera.StreamCapabilitiesInfo[] streamCapabilitiesInfo);

            if (!result.IsOk)
            {
                Log("Could not get Stream capabilities Info.");
                return false;
            }

            streamCapabilities = new List<MLCamera.StreamCapability>();

            for (int i = 0; i < streamCapabilitiesInfo.Length; i++)
            {
                foreach (var streamCap in streamCapabilitiesInfo[i].StreamCapabilities)
                {
                    streamCapabilities.Add(streamCap);
                }
            }

            return streamCapabilities.Count > 0;
        }

        /// <summary>
        /// Handles the event of a new image getting captured.
        /// </summary>
        /// <param name="capturedFrame">Captured Frame.</param>
        /// <param name="resultExtras">Result Extra.</param>
        private void OnCaptureRawVideoFrameAvailable(MLCamera.CameraOutput capturedFrame,
                                                    MLCamera.ResultExtras resultExtras,
                                                    MLCamera.Metadata metadata)
        {
            capturedFrameInfo = capturedFrame;
            latestYUVPlane = capturedFrameInfo.Planes[0];
            cameraTransformAtLatestCapture = cam.transform;
            intrinsics = resultExtras.Intrinsics;
        }

        void DisplayData(MLCamera.IntrinsicCalibrationParameters cameraParameters)
        {
            Debug.LogFormat("Width: {0}", cameraParameters.Width);
            Debug.LogFormat("Height: {0}", cameraParameters.Height);
            Debug.LogFormat("FocalLength: {0}", cameraParameters.FocalLength);
            Debug.LogFormat("PrincipalPoint: {0}", cameraParameters.PrincipalPoint);
            Debug.LogFormat("FOV: {0}", cameraParameters.FOV);
            int index = 0;
            foreach (double dist in cameraParameters.Distortion)
            {
                Debug.LogFormat("Distortion({0}): {1}", index, dist);
                index++;
            }
        }

        private void Log(string message)
        {
            if (verboseDebugLogging) Debug.Log($"MLCDP: {message}");
        }

        private void Abort(string errorMessage, params object[] args)
        {
            Debug.LogErrorFormat($"MLCDP aborting: {errorMessage}", args);
            enabled = false;
        }
    }
}
#endif