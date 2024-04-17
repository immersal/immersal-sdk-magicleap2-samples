/*===============================================================================
Copyright (C) 2024 Immersal - Part of Hexagon. All Rights Reserved.

This file is part of the Immersal SDK.

The Immersal SDK cannot be copied, distributed, or made available to
third-parties for commercial purposes without written permission of Immersal Ltd.

Contact sdk@immersal.com for licensing requests.
===============================================================================*/

using System;
using UnityEngine;

namespace Immersal.XR
{
    public struct CaptureData
    {
        public IntPtr PixelBuffer;
        public int Width;
        public int Height;
        public Vector4 Intrinsics;  // x = principal point x, y = principal point y, z = focal length x, w = focal length y
        public Transform CameraTransform;
        public double[] Distortion; // not yet used
    }

    public interface ICameraDataProvider
    {
        bool TryAcquireLatestData(out CaptureData data);
        bool TryAcquirePngBytes(out byte[] pngBytes, out CaptureData data);
    }
}