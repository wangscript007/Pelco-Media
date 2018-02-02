﻿//
// Copyright (c) 2018 Pelco. All rights reserved.
//
// This file contains trade secrets of Pelco.  No part may be reproduced or
// transmitted in any form by any means or for any purpose without the express
// written permission of Pelco.
//
namespace Pelco.Media.RTSP
{
    /// <summary>
    /// RTSP transports types
    /// </summary>
    public enum TransportType
    {
        Unknown,
        UdpUnicast,
        UdpMulticast,
        RtspInterleaved,
    }
}
