﻿using System;
using Ews.Common;

namespace EboIotEdgeConnector.Extension
{
    public class Signal
    {
        #region PointName
        public string PointName { get; set; }
        #endregion
        #region DatabasePath
        public string DatabasePath { get; set; }
        #endregion
        #region Value
        public string Value { get; set; }
        #endregion
        #region Unit
        public string Unit { get; set; }
        #endregion
        #region SendOnUpdate
        public bool SendOnUpdate { get; set; }
        #endregion
        #region SendTime
        public int SendTime { get; set; }
        #endregion
        #region LastSendTime
        public DateTime? LastSendTime { get; set; }
        #endregion
        #region LastUpdateTime
        public DateTime? LastUpdateTime { get; set; }
        #endregion
        #region EwsId
        public string EwsId => $"01{DatabasePath}";
        #endregion
        #region EwsIdForWrite
        public string EwsIdForWrite {
            get
            {
                // TODO: As more of these types of things are discovered, add them here..
                if (EwsId.Contains("/IO Bus/"))
                {
                    return $"11{DatabasePath}/RequestedValue";
                }
                return EwsId;
            }
        }
        #endregion
        #region Type
        public EwsValueTypeEnum Type { get; set; }
        #endregion
    }
}