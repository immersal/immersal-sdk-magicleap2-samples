#if !UNITY_IOS
/*===============================================================================
Copyright (C) 2024 Immersal - Part of Hexagon. All Rights Reserved.

This file is part of the Immersal SDK.

The Immersal SDK cannot be copied, distributed, or made available to
third-parties for commercial purposes without written permission of Immersal Ltd.

Contact sdk@immersal.com for licensing requests.
===============================================================================*/

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        //Prevent accessing the pixelBuffer in two places as once 
        //private readonly object bufferLock = new object();

        #region Lastest Capture Data Data

        private bool didGetFirstFrame;
        private MLCamera.CameraOutput capturedFrameInfo;
        private MLCamera.IntrinsicCalibrationParameters? intrinsics;
        private byte[] pixelBuffer;
        private Transform cameraTransformAtLatestCapture;

        #endregion

        #region Capture Config

        private int targetImageWidth = 1920;
        private int targetImageHeight = 1080;
        private MLCamera.CaptureFrameRate targetFrameRate = MLCameraBase.CaptureFrameRate._30FPS;
        private MLCamera.CaptureType captureType = MLCamera.CaptureType.Video;
        private MLCamera.Identifier cameraIdentifier = MLCamera.Identifier.CV;

        #endregion

        #region Magic Leap Camera Info

        //The connected Camera
        private MLCamera captureCamera;

        //Cached version of all the available streams and their sizes 
        private MLCameraBase.StreamCapability[] streamCapabilities;

        // True if CaptureVideoStartAsync was called successfully
        private bool isCapturingVideo = false;

        #endregion

        private readonly MLPermissions.Callbacks permissionCallbacks = new MLPermissions.Callbacks();

        private void Awake()
        {
            permissionCallbacks.OnPermissionGranted += OnPermissionGranted;
            permissionCallbacks.OnPermissionDenied += OnPermissionDenied;
            permissionCallbacks.OnPermissionDeniedAndDontAskAgain += OnPermissionDenied;

            isCapturingVideo = false;
            var cameraPose = new GameObject("cameraTransformAtLatestCapture");
            DontDestroyOnLoad(cameraPose);
            cameraTransformAtLatestCapture = cameraPose.transform;
            MLPermissions.RequestPermission(MLPermission.Camera, permissionCallbacks);
        }

        private void OnDisable()
        {
            DisconnectCamera();
        }

        private bool TryAcquireIntrinsics(out Vector4 intr, out double[] dist)
        {
            intr = Vector4.zero;
            dist = new double[5];

            if (this.intrinsics is not { } intrinsicsValue)
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

            if (capturedFrameInfo.Planes is null || capturedFrameInfo.Planes.Length <= 0) return false;

            var latestYUVPlane = capturedFrameInfo.Planes[0];
            if (latestYUVPlane.Data is null || latestYUVPlane.Data.Length <= 0) return false;
            if (!TryAcquireIntrinsics(out Vector4 intrinsics, out double[] distortion)) return false;

            GetUnpaddedBytes(latestYUVPlane, false, out byte[] processedImageData);

            if (processedImageData is null || processedImageData.Length <= 0) return false;

            unsafe
            {
                fixed (byte* pinnedData = processedImageData)
                {
                    data.PixelBuffer = (IntPtr) pinnedData;
                }
            }

            data.CameraTransform = cameraTransformAtLatestCapture;
            data.Intrinsics = intrinsics;
            data.Distortion = distortion;
            data.Width = (int) latestYUVPlane.Width;
            data.Height = (int) latestYUVPlane.Height;

            return data.PixelBuffer != IntPtr.Zero;
        }

        public bool TryAcquirePngBytes(out byte[] pngBytes, out CaptureData data)
        {
            data = default;
            pngBytes = null;

            if (capturedFrameInfo.Planes is null || capturedFrameInfo.Planes.Length <= 0) return false;
            var latestYUVPlane = capturedFrameInfo.Planes[0];
            if (latestYUVPlane.Data is null || latestYUVPlane.Data.Length <= 0) return false;
            if (!TryAcquireIntrinsics(out Vector4 intrinsics, out double[] distortion)) return false;

            data.CameraTransform = cameraTransformAtLatestCapture;
            data.Intrinsics = intrinsics;
            data.Distortion = distortion;
            data.Width = (int) latestYUVPlane.Width;
            data.Height = (int) latestYUVPlane.Height;

            GetUnpaddedBytes(latestYUVPlane, true, out byte[] grayBytes);

            const uint channel = 1;
            pngBytes = ImageConversion.EncodeArrayToPNG(grayBytes, pngFormat, latestYUVPlane.Width,
                latestYUVPlane.Height, latestYUVPlane.Width * channel);

            return !(pngBytes is null || pngBytes.Length <= 0);
        }

        private void GetUnpaddedBytes(MLCamera.PlaneInfo yBuffer, bool invertVertically, out byte[] outputBuffer)
        {
          //  lock (bufferLock)
            {
                byte[] data = yBuffer.Data;
                int width = (int) yBuffer.Width;
                int height = (int) yBuffer.Height, size = width * height;
                int stride = invertVertically ? -(int) yBuffer.Stride : (int) yBuffer.Stride;
                int invertStartOffset = ((int) yBuffer.Stride * height) - (int) yBuffer.Stride;

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
                        if (width > 0 && height > 0)
                        {
                            UnsafeUtility.MemCpyStride(dstPtr, width, srcPtr, stride, width, height);
                        }
                    }
                }

                outputBuffer = pixelBuffer;
            }
        }

        private void OnPermissionGranted(string permission)
        {
#if UNITY_ANDROID
            MLPluginLog.Debug($"Granted {permission}.");
            TryEnableMLCamera();
#endif
        }

        private void OnDestroy()
        {
            DisconnectCamera();
        }

        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused)
            {
                DisconnectCamera();
            }
            else if (!IsCameraConnected && MLPermissions.CheckPermission(MLPermission.Camera).IsOk)
            {
                TryEnableMLCamera();
            }
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

        private async void TryEnableMLCamera()
        {
            if (!MLPermissions.CheckPermission(MLPermission.Camera).IsOk)
                return;
            await EnableMLCameraAsync();
        }

        /// <summary>
        /// Connects the MLCamera component and instantiates a new instance
        /// if it was never created.
        /// </summary>
        private async Task EnableMLCameraAsync()
        {
            bool cameraDeviceAvailable = false;
            while (!cameraDeviceAvailable)
            {
                MLResult result =
                    MLCamera.GetDeviceAvailabilityStatus(cameraIdentifier, out cameraDeviceAvailable);
                if (!(result.IsOk && cameraDeviceAvailable))
                {
                    // Wait until the camera device is available
                    await Task.Delay(TimeSpan.FromSeconds(1.0f));
                }
            }

            // Camera device is available now
            Debug.Log("Camera device is available.");
            await StartCameraCaptureAsync();
        }

        private async Task StartCameraCaptureAsync()
        {
            try
            {
                Debug.Log("StartCameraCaptureAsync started.");

                MLCameraBase.ConnectContext context = new MLCameraBase.ConnectContext();

                context = CreateCameraContext();

                captureCamera = await MLCamera.CreateAndConnectAsync(context);
                if (captureCamera == null)
                {
                    Debug.LogError("Could not create or connect to a valid camera. Stopping Capture.");
                    return;
                }

                Debug.Log("Camera Connected");

                bool hasImageStreamCapabilities = GetImageStreamCapabilities();
                if (!hasImageStreamCapabilities)
                {
                    Debug.LogError("Could not start capture. No valid Image Streams available. Disconnecting Camera");
                    await DisconnectCameraAsync();
                    return;
                }

                captureCamera.OnRawVideoFrameAvailable += OnCaptureRawVideoFrameAvailable;

                MLCameraBase.CaptureConfig captureConfig = new MLCameraBase.CaptureConfig();
                captureConfig = CreateCaptureConfig();
                Debug.Log("CreateCaptureConfig");

                bool captureStarted = await PrepareAndStartCapture(captureConfig);
                if (!captureStarted)
                {
                    Debug.LogError("Could not start capture. Disconnecting Camera");
                    await DisconnectCameraAsync();
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in StartCameraCaptureAsync: {ex.Message}");
                throw;
            }

            Debug.Log("StartCameraCaptureAsync completed.");
        }

        private MLCamera.ConnectContext CreateCameraContext()
        {
            var context = MLCamera.ConnectContext.Create();
            context.CamId = cameraIdentifier;
            context.Flags = MLCamera.ConnectFlag.CamOnly;
            context.EnableVideoStabilization = false;
            return context;
        }

        private MLCamera.CaptureConfig CreateCaptureConfig()
        {
            var captureConfig = new MLCamera.CaptureConfig();
            captureConfig.CaptureFrameRate = targetFrameRate;
            captureConfig.StreamConfigs = new MLCamera.CaptureStreamConfig[1];
            captureConfig.StreamConfigs[0] =
                MLCamera.CaptureStreamConfig.Create(GetStreamCapability(), MLCamera.OutputFormat.YUV_420_888);
            return captureConfig;
        }

        private async Task<bool> PrepareAndStartCapture(MLCamera.CaptureConfig captureConfig)
        {
            var prepareResult = captureCamera.PrepareCapture(captureConfig, out MLCamera.Metadata _);
            if (!MLResult.DidNativeCallSucceed(prepareResult.Result, nameof(captureCamera.PrepareCapture)))
            {
                Debug.LogError($"Could not start. Result: {prepareResult.Result}");
                return false;
            }

            //Prepare auto exposure and white balance
            var aeawbResult = await captureCamera.PreCaptureAEAWBAsync();

            if (aeawbResult.IsOk && captureType == MLCamera.CaptureType.Video)
            {
                var startCapture = await captureCamera.CaptureVideoStartAsync();
                isCapturingVideo =
                    MLResult.DidNativeCallSucceed(startCapture.Result, nameof(captureCamera.CaptureVideoStart));
                if (!isCapturingVideo)
                {
                    Debug.LogError($"Could not start camera capture. Result : {startCapture.Result}");
                    return false;
                }
            }

            return true;
        }

        private async Task DisconnectCameraAsync()
        {
            if (captureCamera != null)
            {
                if(isCapturingVideo)
                    await captureCamera.CaptureVideoStopAsync();

                await captureCamera.DisconnectAsync();
                captureCamera.OnRawVideoFrameAvailable -= OnCaptureRawVideoFrameAvailable;
                captureCamera = null;
                streamCapabilities = null;
            }
        }

        /// <summary>
        /// Disconnects the camera.
        /// </summary>
        private void DisconnectCamera()
        {
            if (captureCamera == null || !IsCameraConnected)
                return;

            if (isCapturingVideo)
                captureCamera.CaptureVideoStop();

            captureCamera.Disconnect();
            captureCamera.OnRawVideoFrameAvailable -= OnCaptureRawVideoFrameAvailable;
            streamCapabilities = null;
            captureCamera = null;
        }

        /// <summary>
        /// Gets currently selected StreamCapability
        /// </summary>
        private MLCamera.StreamCapability GetStreamCapability()
        {
            if (MLCamera.TryGetBestFitStreamCapabilityFromCollection(streamCapabilities, targetImageWidth,
                    targetImageHeight, MLCameraBase.CaptureType.Video,
                    out MLCameraBase.StreamCapability streamCapability))
            {
                Log($"{streamCapability.ToString()} is selected!");
                return streamCapability;
            }

            Log($"{streamCapabilities[0]} is selected!");
            return streamCapabilities[0];
        }

        /// <summary>
        /// Gets the Image stream capabilities.
        /// </summary>
        /// <returns>True if MLCamera returned at least one stream capability.</returns>
        private bool GetImageStreamCapabilities()
        {
            if (captureCamera == null)
            {
                Log("Could not get Stream capabilities Info. No Camera Connected");
                return false;
            }
            streamCapabilities =
                MLCamera.GetImageStreamCapabilitiesForCamera(captureCamera, MLCameraBase.CaptureType.Video);

            return streamCapabilities.Length > 0;
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
            MLResult result = MLCVCamera.GetFramePose(resultExtras.VCamTimestamp, out Matrix4x4 outMatrix);
            if (result.IsOk)
            {
                capturedFrameInfo = capturedFrame;
                cameraTransformAtLatestCapture.position = outMatrix.GetPosition();
                cameraTransformAtLatestCapture.rotation = outMatrix.rotation;
                intrinsics = resultExtras.Intrinsics;
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