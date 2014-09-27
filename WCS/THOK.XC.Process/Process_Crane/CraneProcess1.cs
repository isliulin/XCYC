﻿using System;
using System.Collections.Generic;
using System.Text;
using THOK.MCP;
using System.Data;
using THOK.XC.Process.Dal;
using System.Threading;

namespace THOK.XC.Process.Process_Crane
{
    public class CraneProcess1 : AbstractProcess
    {
        private DataTable dtCrane;
        private DataTable dtCraneList;
        //堆垛机状态表  ""，表示状态未知，发送报文获取堆垛机状态。 0：空闲，1：执行中
        private Dictionary<string, string> dCraneState = new Dictionary<string, string>();
        private Dictionary<string, string> dCraneMode = new Dictionary<string, string>();
        //还没接收到堆垛机CSR报文时，先把任务暂存在dCraneWait字典
        private Dictionary<string, DataRow> dCraneWait = new Dictionary<string, DataRow>();
        private Dictionary<string, string> dCraneError = new Dictionary<string, string>();
        private DataTable dtSendCRQ;
        private DataTable dtErrMesage;
        private int NCK001;
        //二楼出库是否排序，参数控制。
        private bool blnOutOrder = true;
        private string lastCraneNo = "";
        private bool blnConnect = false;
        object sendlocker = new object();
        object ARQlocker = new object();

        //process.Initialize(context);初始化的时候执行
        public override void Initialize(Context context)
        {
            try
            {
                base.Initialize(context);
                //堆垛机收发报文的错误信息代码
                CraneErrMessageDal errDal = new CraneErrMessageDal();
                dtErrMesage = errDal.GetErrMessageList();
                dtCraneList = errDal.GetCraneList();
                NCK001 = 100;

                for (int i = 0; i < dtCraneList.Rows.Count; i++)
                {
                    string CraneNo = dtCraneList.Rows[i]["CRANE_NO"].ToString();
                    if (!dCraneWait.ContainsKey(CraneNo))
                    {
                        dCraneWait.Add(CraneNo, null);
                    }
                    if (!dCraneState.ContainsKey(CraneNo))
                    {
                        dCraneState.Add(CraneNo, "");
                    }
                    if (!dCraneMode.ContainsKey(CraneNo))
                    {
                        dCraneMode.Add(CraneNo, "");
                    }
                    if (!dCraneError.ContainsKey(CraneNo))
                    {
                        dCraneError.Add(CraneNo, "000");
                    }
                }

                THOK.MCP.Config.Configuration conf = new MCP.Config.Configuration();
                conf.Load("Config.xml");
                blnOutOrder = conf.Attributes["IsOutOrder"] == "1" ? true : false;
            }
            catch (Exception ex)
            {
                Logger.Error("THOK.XC.Process.Process_Crane.CraneProcess堆垛机初始化出错，原因：" + ex.Message);
            }
        }
        protected override void StateChanged(StateItem stateItem, IProcessDispatcher dispatcher)
        {
            /*  处理事项：
             * 
             *  堆垛机任务处理
             *  出库任务传入Task 需要产生TaskDetail，并更新起始位置及目的地。
             *  入库任务传入TaskDetail 
             *  Init - 初始化。
             *      FirstBatch - 生成第一批入库请求任务。
             *      StockInRequest - 根据请求，生成入库任务。
             * 
             *  stateItem.State ：参数 - 请求的卷烟编码。        
            */
            try
            {
                switch (stateItem.ItemName)
                {
                    //开始出库，主动调用。
                    case "StockOutRequest": 
                        DataTable[] dtSend = (DataTable[])stateItem.State;
                        if (dtSend[1] != null)
                        {
                            InsertCraneQuene(dtSend[1]);
                        }
                        InsertCraneQuene(dtSend[0]);
                        //线程调度堆垛机
                        CraneThreadStart();
                        break;
                        
                    //二楼出库RFID校验,Task_Detail ItemNo=1 状态更新为2,确认检验完成后，才可下下一产品的下一任务
                    case "StockOutToCarStation": 
                        string  TaskID = (string)stateItem.State;
                        if (dtCrane != null)
                        {
                            DataRow[] drs = dtCrane.Select(string.Format("TASK_ID='{0}'", TaskID));
                            if (drs.Length > 0)
                            {
                                dtCrane.Rows.Remove(drs[0]);
                            }

                            TaskDal tdal = new TaskDal();
                            //ItemNo=1 状态更新为2
                            tdal.UpdateTaskDetailState(string.Format("TASK_ID='{0}' AND ITEM_NO=1", TaskID), "2");

                            //更新完成之后，线程调用堆垛机，避免堆垛机因调度原因而是堆垛机没有任务。
                            CraneThreadStart();
                        }
                        break;
                    //货物到达入库站台，调用堆垛机
                    case "CraneInRequest":
                        DataTable dtInCrane = (DataTable)stateItem.State;
                        InsertCraneQuene(dtInCrane);
                        SendTelegram(dtInCrane.Rows[0]["CRANE_NO"].ToString(), dtInCrane.Rows[0]);
                        break;
                    case "SingleCraneTask":
                        SendTelegram(stateItem.State.ToString(), null);
                        break;
                    case "ARQ":
                        DataRow dr = (DataRow)stateItem.State;
                        SendTelegramARQ(dr, true);
                        break;
                    case "ACP":
                        ACP(stateItem.State);
                        break;
                    case "CSR":
                        CSR(stateItem.State);
                        break;
                    case "ACK":
                        ACK(stateItem.State);
                        break;
                    case "DUM":
                        SendDUA();
                        break;
                    case "NCK":
                        NCK(stateItem.State);
                        break;
                    case "DEC":
                        DEC(stateItem.State);
                        break;
                    case "Connect":
                        blnConnect = true;
                        break;
                    case "Disconnect":
                        blnConnect = false;
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Error("THOK.XC.Process.Process_Crane.CraneProcess，原因：" + e.Message);
            }
        }


        #region 其它函数
        /// <summary>
        /// 发送报文，并返回发送成功。
        /// </summary>
        /// <param name="CraneNo"></param>
        /// <param name="TaskID"></param>
        /// <returns></returns>
        private bool SendTelegram(string CraneNo, DataRow drTaskID)
        {
            //添加一个对象作为锁

            lock (sendlocker)
            {
                bool blnSend = false;
                //判断dCraneState[CraneNo] 是否空闲；
                if (!dCraneState.ContainsKey(CraneNo))
                {
                    dCraneState.Add(CraneNo, "");
                    if (this.blnConnect)
                        SendTelegramCRQ(CraneNo);
                }

                //状态在没有收到CSR的时候，dCraneState[CraneNo]=""
                if (string.IsNullOrEmpty(dCraneState[CraneNo]))  //等待堆垛机应答。
                {
                    if (dCraneWait[CraneNo] == null && drTaskID != null)
                    {
                        dCraneWait[CraneNo] = drTaskID;
                        return false;
                    }
                    return false;
                }
                //堆垛机正忙。
                if (dCraneState[CraneNo].Length > 0 && dCraneState[CraneNo] != "00000000")
                    return true;
                //堆垛机非自动模式。
                if (dCraneMode[CraneNo].Length > 0 && dCraneMode[CraneNo] != "1")
                    return true;

                //堆垛机空闲，自行找任务执行
                TaskDal dal = new TaskDal();
                DataRow drTaskCrane = null;

                //出库任务调用堆垛机，drTaskID无任务，自行找出任务
                if (drTaskID == null && dtCrane != null)
                {
                    //读取二楼出库站台是否有烟包，PLC
                    //按照任务等级，任务时间，产品形态，
                    //FORDERBILLNO 如果有校验不合格，需另外批次替代，产生的新出库单，原始出库单号FORDERBILLNO,排序不会乱掉
                    //DataRow[] drs = dtCrane.Select(string.Format("CRANE_NO='{0}' and STATE=0 and TASK_TYPE in ('12','22','13','14')", CraneNo), "TASK_LEVEL,TASK_DATE,BILL_NO,IS_MIX,PRODUCT_CODE,TASK_ID");
                    DataRow[] drs = dtCrane.Select(string.Format("CRANE_NO='{0}' AND STATE='0' ", CraneNo), "TASK_LEVEL DESC,FORDER,PRODUCT_CODE,TASK_ID");

                    for (int i = 0; i < drs.Length; i++)
                    {
                        //如果是二楼出库,判断当前任务的产品是不是
                        if (drs[i]["TASK_TYPE"].ToString() == "22")
                        {
                            //判断是否能出库
                            string ForderBillNo = drs[i]["FORDERBILLNO"].ToString();
                            int FOrder = int.Parse(drs[i]["FORDER"].ToString());
                            string IsMix = drs[i]["IS_MIX"].ToString();
                            //判断能否出库
                            //bool blnCan = dal.ProductCanToCar(ForderBillNo, FOrder, IsMix, false, blnOutOrder);
                            bool blnCan = dal.ProductOutToStation(ForderBillNo, FOrder, CraneNo, blnOutOrder);
                            if (!blnCan)
                                continue;
                        }
                        drTaskCrane = drs[i];
                        break;
                    }
                }
                else  //根据任务编号发送报文。
                {
                    drTaskCrane = drTaskID;
                }

                if (drTaskCrane != null)
                {
                    string TaskType = drTaskCrane["TASK_TYPE"].ToString();
                    //Task_Detail ITEM_NO字段
                    //TaskType:13盘点 14移库
                    string ItemNo = drTaskCrane["ITEM_NO"].ToString();
                    //正常1，2楼出库、1楼盘点移库(非同巷道)
                    if (TaskType.Substring(1, 1) == "2" || (TaskType == "13" && ItemNo == "1") || (TaskType == "14" && ItemNo == "1" && drTaskCrane["CRANE_NO"].ToString() != drTaskCrane["NEW_CRANE_NO"].ToString()))
                    {
                        //读取出库站台有无货物,如有直接返回
                        object t = ObjectUtil.GetObject(WriteToService(drTaskCrane["SERVICE_NAME"].ToString(), drTaskCrane["ITEM_NAME_1"].ToString()));//读取当前出库站台是否有货位
                        if (t.ToString() != "0")
                        {
                            blnSend = false;
                            return blnSend;
                        }
                    }
                    //出库任务，以堆垛机为第一步路线
                    if (ItemNo == "1")
                    {
                        //产生TASK_DETAIL明细,并返回TASKNO
                        string strTaskDetailNo = "";
                        try
                        {
                            strTaskDetailNo = dal.InsertTaskDetail(drTaskCrane["TASK_ID"].ToString());
                        }
                        catch (Exception ed)
                        {
                            Console.WriteLine(ed.Message);
                            return false;
                        }

                        DataRow[] drs = dtCrane.Select(string.Format("TASK_ID='{0}'", drTaskCrane["TASK_ID"]));
                        if (drs.Length > 0)
                        {
                            drs[0].BeginEdit();
                            drs[0]["TASK_NO"] = strTaskDetailNo;
                            drs[0]["ASSIGNMENT_ID"] = strTaskDetailNo.PadLeft(8, '0');
                            drs[0].EndEdit();
                            dtCrane.AcceptChanges();
                            drTaskCrane["ASSIGNMENT_ID"] = strTaskDetailNo.PadLeft(8, '0');
                        }
                    }
                    SendTelegramARQ(drTaskCrane, true);//发送报文
                    blnSend = true;
                }

                return blnSend;
            }
        }

        /// <summary>
        ///  接收ACP后，根据获取的任务类型，重新获取新的TaskID;
        /// </summary>
        /// <param name="CraneNo"></param>
        /// <param name="TaskType"></param>
        /// <returns></returns>
        ///    1、 出库类型，如果是出库，则先判断所在层是否有入库任务，若有则执行，没有则判断另一楼层的入库任务，若有则执行。若没有需要先判断一楼是否有出库，有则先执行，否则继续执行下一条出库任务。
        ///    2、入库类型，判断一楼是否有出库，若有则执行，没有判断二楼是否有出库，有则执行出库。没有则执行执行入库计划。 
        private void GetNextTaskID(string CraneNo, string TaskType)
        {
            DataRow[] drs;
            bool blnSend = false;
            string type = TaskType.PadRight(2, '0').Substring(1, 1);
            switch (type)
            {
                case "1":
                case "3":
                    blnSend = false;
                    blnSend = SendTelegram(CraneNo, null);  //查询出库报文
                    
                    break;
                case "2":
                    blnSend = false;
                    if (TaskType == "12")
                    {
                        if (!blnSend)
                        {
                            drs = dtCrane.Select(string.Format("CRANE_NO='{0}' AND STATE=0 AND TASK_TYPE in ('11','13','14')", CraneNo), "TASK_LEVEL DESC");
                            if (drs.Length > 0)
                            {
                                blnSend = SendTelegram(CraneNo, drs[0]);
                            }
                        }
                        if (!blnSend)
                        {
                            drs = dtCrane.Select(string.Format("CRANE_NO='{0}' AND STATE=0 AND TASK_TYPE='21'", CraneNo));
                            if (drs.Length > 0)
                            {
                                blnSend = SendTelegram(CraneNo, drs[0]);
                            }
                        }
                    }
                    else
                    {
                        if (!blnSend)
                        {
                            drs = dtCrane.Select(string.Format("CRANE_NO='{0}' AND STATE=0 AND TASK_TYPE='21'", CraneNo));
                            if (drs.Length > 0)
                            {
                                blnSend = SendTelegram(CraneNo, drs[0]);
                            }
                        }
                        if (!blnSend)
                        {
                            drs = dtCrane.Select(string.Format("CRANE_NO='{0}' AND STATE=0 AND TASK_TYPE  in ('11','13','14') ", CraneNo));
                            if (drs.Length > 0)
                            {
                                blnSend = SendTelegram(CraneNo, drs[0]);
                            }
                        }

                    }
                    if (!blnSend)
                        blnSend = SendTelegram(CraneNo, null);
                    break;
            }
        }

        /// <summary>
        /// 插入dtCrane
        /// </summary>
        /// <param name="dt"></param>
        private void InsertCraneQuene(DataTable dt)
        {
            if (dtCrane == null)
            {
                dtCrane = dt.Clone();
            }
            DataRow[] drs = dt.Select("", "TASK_LEVEL desc,IS_MIX,FORDER,PRODUCT_CODE,TASK_ID");
            object[] obj = new object[dt.Columns.Count]; 
            for (int i = 0; i < drs.Length; i++)
            {
                DataRow[] drsExist = dtCrane.Select(string.Format("TASK_ID='{0}'", drs[i]["TASK_ID"]));
                if (drsExist.Length > 0)
                    continue;
                
                drs[i].ItemArray.CopyTo(obj,0);
                dtCrane.Rows.Add(obj);
            }
            dtCrane.AcceptChanges();
        }

        /// <summary>
        /// 多线程调用6台堆垛机。
        /// </summary>
        private void CraneThreadStart()
        {
            //ThreadStart starter1 = delegate { SendTelegram("01", null); };
            //new Thread(starter1).Start();
            for (int i = 0; i < dtCraneList.Rows.Count; i++)
            {
                string CraneNo = dtCraneList.Rows[i]["CRANE_NO"].ToString();
                SendTelegram(CraneNo, null);
            }
        }
        #endregion  
       
        #region 处理接堆垛机发送的报文
        private void ACP(object state)
        {
            Dictionary<string, string> msg = (Dictionary<string, string>)state;
            SendACK(msg);

            TaskDal dal = new TaskDal();

            DataRow dr = null;
            if (dtCrane != null)
            {
                //DataRow[] drs = dtCrane.Select(string.Format("SQUENCE_NO='{0}'", msg["SeqNo"]));
                DataRow[] drs = dtCrane.Select(string.Format("ASSIGNMENT_ID='{0}'", msg["AssignmentID"]));
                if (drs.Length > 0)
                {
                    dr = drs[0];
                }
            }
            //如果在datatbale中找不到，再从数据库里查找
            if (dr == null)
            {
                //根据流水号，获取资料
                //DataTable dt = dal.CraneTaskIn(string.Format("DETAIL.SQUENCE_NO='{1}' AND DETAIL.CRANE_NO='{0}'", msg["CraneNo"], msg["SeqNo"]));
                DataTable dt = dal.CraneTaskIn(string.Format("DETAIL.ASSIGNMENT_ID='{1}' AND DETAIL.CRANE_NO='{0}'", msg["CraneNo"], msg["AssignmentID"]));
                if (dt.Rows.Count > 0)
                    dr = dt.Rows[0];
            }
            string TaskType = "";
            string TaskID = ""; 
            string ItemNo = "";
            if (dr != null)
            {
                TaskType = dr["TASK_TYPE"].ToString();
                TaskID = dr["TASK_ID"].ToString();
                ItemNo = dr["ITEM_NO"].ToString();

                //判断暂存的任务是否是当前完成的任务，如果是清空暂存
                if (dCraneWait[msg["CraneNo"]] != null)
                {
                    if (dCraneWait[msg["CraneNo"]]["TASK_ID"].ToString() == TaskID)
                    {
                        dCraneWait[msg["CraneNo"]] = null;
                    }
                }
            }            

            dCraneError[msg["CraneNo"]] = msg["ReturnCode"];
            if (msg["ReturnCode"] == "000")
            {
                Logger.Info("堆垛机" + msg["CraneNo"] + "任务：" + msg["AssignmentID"] + "完成");

                lock (dCraneState)
                {
                    dCraneState[msg["CraneNo"]] = "00000000";
                }

                if (dr != null)
                {
                    TaskFinishUpdate(dr);
                    //移除完成的任务
                    if (dtCrane != null)
                    {
                        DataRow[] drs = dtCrane.Select(string.Format("TASK_ID='{0}'", TaskID));
                        if (drs.Length > 0)
                        {
                            dtCrane.Rows.Remove(drs[0]);
                        }
                    }
                    
                    //查找发送下条报文。
                    GetNextTaskID(msg["CraneNo"], TaskType);
                }
            }
            else if(msg["ReturnCode"] == "001")
            {
                lock (dCraneState)
                {
                    //dCraneState[msg["CraneNo"]] = "00000000";
                }
            }
            else
            {
                string ErrMsg = "";
                DataRow[] drMsgs = dtErrMesage.Select(string.Format("CODE='{0}'", msg["ReturnCode"]));
                if (drMsgs.Length > 0)
                    ErrMsg = drMsgs[0]["DESCRIPTION"].ToString();

                //更新堆垛机错误编号
                dal.UpdateCraneErrCode(TaskID, ItemNo, msg["ReturnCode"]);
                Logger.Error(string.Format("堆垛机{0}返回错误代码{1}:{2}", msg["CraneNo"], msg["ReturnCode"], ErrMsg));               
            }
            
            CraneErrWriteToPLC(msg["CraneNo"], int.Parse(msg["ReturnCode"]));
        }
        /// <summary>
        /// 堆垛机完成任务后，更新数据库
        /// </summary>
        /// <param name="CraneNo"></param>
        /// <param name="ErrCode"></param>
        private void TaskFinishUpdate(DataRow dr)
        {
            //根据流水号，获取资料
            TaskDal dal = new TaskDal();
            string filter = "";
            string TaskType = "";
            string TaskID = "";
            string ItemNo = "";
            string CellCode = "";
            string NewCellCode = "";
            string FromStation = "";
            string ToStation = "";
            string NewStation = "";
            int TaskNo = 0;

            string BillNo = "";
            int ProductType = 0;
            string ProductCode = "";
            string Barcode = "";
            string PalletCode = "";
            string ServiceName = "";
            string ItemName2 = "";
            string CraneNo = "";
            string CraneNo2 = "";
            if (dr != null)
            {
                TaskType = dr["TASK_TYPE"].ToString();
                TaskID = dr["TASK_ID"].ToString();
                ItemNo = dr["ITEM_NO"].ToString();
                CellCode = dr["CELL_CODE"].ToString();
                NewCellCode = dr["NEWCELL_CODE"].ToString();
                FromStation = dr["STATION_NO"].ToString();
                if (TaskType == "22")
                    ToStation = dr["MEMO"].ToString();
                else
                    ToStation = dr["TARGET_CODE"].ToString();
                NewStation = dr["NEW_TARGET_CODE"].ToString();
                TaskNo = int.Parse(dr["TASK_NO"].ToString());
                BillNo = dr["BILL_NO"].ToString();
                ProductType = int.Parse(dr["PRODUCT_TYPE"].ToString());
                ProductCode = dr["PRODUCT_CODE"].ToString();
                Barcode = dr["PRODUCT_BARCODE"].ToString();
                PalletCode = dr["PALLET_CODE"].ToString();
                ServiceName = dr["SERVICE_NAME"].ToString();
                ItemName2 = dr["ITEM_NAME_2"].ToString();
                CraneNo = dr["CRANE_NO"].ToString();
                CraneNo2 = dr["NEW_CRANE_NO"].ToString();
                filter = string.Format("TASK_ID='{0}' and ITEM_NO='{1}'", TaskID, ItemNo);

                string UpdateFilter = "";
                //更新堆垛机执行完成
                if (TaskType != "22")
                    dal.UpdateTaskDetailState(filter, "2");

                //出库，一楼盘点出库                
                if (TaskType.Substring(1, 1) == "2" || (TaskType == "13" && ItemNo == "1"))
                {
                    WriteToPLC(TaskNo, int.Parse(ToStation), ProductType, Barcode, PalletCode, ServiceName, ItemName2);
                    //一楼出库
                    if (TaskType == "12")
                    {
                        CellDal Cdal = new CellDal();
                        //货位解锁
                        Cdal.UpdateCellOutFinishUnLock(CellCode);
                        //更新PRODUCTSTATE 出库单号
                        ProductStateDal psdal = new ProductStateDal();
                        psdal.UpdateOutBillNo(TaskID);
                    }

                    //更新TASK_DETAIL FROM_STATION TO_STATION STATE
                    UpdateFilter = string.Format("TASK_ID='{0}' AND ITEM_NO=2", TaskID);
                    dal.UpdateTaskDetailStation(FromStation, ToStation, "1", UpdateFilter);                    
                }
                //入库完成，更新Task任务完成。
                else if (TaskType.Substring(1, 1) == "1" || (TaskType == "13" && ItemNo == "4"))
                {
                    //更新任务状态。
                    dal.UpdateTaskState(TaskID, "2");
                    CellDal Cdal = new CellDal();
                    Cdal.UpdateCellInFinishUnLock(TaskID);//入库完成，更新货位。
                    Logger.Info("任务类型:" + TaskType + "入库完成,货位已解锁");

                    BillDal billdal = new BillDal();
                    string isBill = "1";
                    if (ProductCode == "0000")
                        isBill = "0";
                    billdal.UpdateInBillMasterFinished(dr["BILL_NO"].ToString(), isBill);//更新表单

                }
                else if (TaskType == "14")
                {
                    //如果目标地址与源地址不同巷道
                    if (CraneNo != CraneNo2)
                    {
                        if (ItemNo == "1")
                        {
                            WriteToPLC(TaskNo, int.Parse(NewStation), ProductType, Barcode, PalletCode, ServiceName, ItemName2);
                            //更新货位信息
                            CellDal Cdal = new CellDal();
                            Cdal.UpdateCellOutFinishUnLock(CellCode);
                            //更新TASK_DETAIL FROM_STATION TO_STATION STATE
                            UpdateFilter = string.Format("TASK_ID='{0}' AND ITEM_NO=2", TaskID);
                            dal.UpdateTaskDetailStation(FromStation, NewStation, "1", UpdateFilter);
                            BillDal billdal = new BillDal();                            
                        }
                        else
                        {
                            //更新任务状态。
                            dal.UpdateTaskState(TaskID, "2");

                            CellDal Cdal = new CellDal();
                            Cdal.UpdateCellInFinishUnLock(NewCellCode);

                            BillDal billdal = new BillDal();
                            string isBill = "1";
                            //if (dr["PRODUCT_CODE"].ToString() == "0000")
                            //    isBill = "0";
                            billdal.UpdateInBillMasterFinished(BillNo, isBill);//更新表单
                        }
                    }
                    else   //相同巷道
                    {
                        //更新任务状态。
                        dal.UpdateTaskState(TaskID, "2");

                        CellDal Cdal = new CellDal();
                        //入库完成，更新货位。
                        Cdal.UpdateCellRemoveFinish(NewCellCode, CellCode);
                        //Cdal.UpdateCellOutFinishUnLock(CellCode);

                        BillDal billdal = new BillDal();
                        string isBill = "1";
                        //if (dr["PRODUCT_CODE"].ToString() == "0000")
                        //    isBill = "0";
                        //更新WMS单据状态
                        billdal.UpdateInBillMasterFinished(BillNo, isBill);
                    }
                }
                //移除完成的任务
                if (dtCrane != null)
                {
                    DataRow[] drs = dtCrane.Select(string.Format("TASK_ID='{0}'", TaskID));
                    if (drs.Length > 0)
                    {
                        dtCrane.Rows.Remove(drs[0]);
                    }
                }

                //查找发送下条报文。
                GetNextTaskID(CraneNo, TaskType);
            }
        }
        /// <summary>
        /// 堆垛机状态。
        /// </summary>
        /// <param name="state"></param>
        private void CSR(object state)
        {
            Dictionary<string, string> msg = (Dictionary<string, string>)state;
            SendACK(msg);

            //堆垛机状态
            if (dCraneState.ContainsKey(msg["CraneNo"]))
                dCraneState[msg["CraneNo"]] = msg["AssignmentID"];
            else
                dCraneState.Add(msg["CraneNo"], "");

            //堆垛机模式
            if (dCraneMode.ContainsKey(msg["CraneNo"]))
                dCraneMode[msg["CraneNo"]] = msg["CraneMode"];
            else
                dCraneMode.Add(msg["CraneNo"], "");


            if (msg["CraneMode"] == "1")
            {
                string status = "空载";
                if (msg["RearForkLeft"] == "LO")
                    status = "负载";
                //Logger.Info("堆垛机" + msg["CraneNo"] + "模式：自动");
                Logger.Info("堆垛机" + msg["CraneNo"] + " 模式：自动 任务：" + msg["AssignmentID"] + " 状态：" + status);
            }
            //else if (msg["CraneMode"] == "2")
            //    Logger.Info("堆垛机" + msg["CraneNo"] + "模式：停止");
            //else
            //    Logger.Info("堆垛机" + msg["CraneNo"] + "模式：手动");


            //如果返回错误代码是000时,CraneMode=1表示堆垛机是自动状态,CraneMode 1: automatic 2: stopped 3: manual
            if (msg["ReturnCode"] == "000")
            {
                if (msg["AssignmentID"] == "00000000" && msg["CraneMode"] == "1")
                {
                    //此堆垛机状态为空闲
                    //dCraneState[msg["CraneNo"]] = "0";

                    if (dCraneWait[msg["CraneNo"]] != null)
                    {
                        SendTelegram(msg["CraneNo"], dCraneWait[msg["CraneNo"]]);
                        dCraneWait[msg["CraneNo"]] = null;
                    }
                    else
                    {
                        SendTelegram(msg["CraneNo"], null);
                    }
                }
                //else
                //    dCraneState[msg["CraneNo"]] = "1";
            }
            else
            {
                TaskDal dal = new TaskDal();
                DataRow dr = null;
                if (dtCrane != null)
                {
                    DataRow[] drs = dtCrane.Select(string.Format("ASSIGNMENT_ID='{0}'", msg["AssignmentID"]));
                    if (drs.Length > 0)
                    {
                        dr = drs[0];
                    }
                }
                //如果在datatbale中找不到，再从数据库里查找
                if (dr == null)
                {
                    //根据流水号，获取资料
                    DataTable dt = dal.CraneTaskIn(string.Format("DETAIL.ASSIGNMENT_ID='{1}' AND DETAIL.CRANE_NO='{0}'", msg["CraneNo"], msg["AssignmentID"]));
                    if (dt.Rows.Count > 0)
                        dr = dt.Rows[0];

                }
                string TaskID = "";
                string ItemNo = "";
                if (dr != null)
                {
                    TaskID = dr["TASK_ID"].ToString();
                    ItemNo = dr["ITEM_NO"].ToString();

                    string ErrMsg = "";
                    DataRow[] drMsgs = dtErrMesage.Select(string.Format("CODE='{0}'", msg["ReturnCode"]));
                    if (drMsgs.Length > 0)
                        ErrMsg = drMsgs[0]["DESCRIPTION"].ToString();
                    Logger.Error(string.Format("堆垛机{0}返回错误代码{1}:{2}", msg["CraneNo"], msg["ReturnCode"], ErrMsg));

                    dal.UpdateCraneErrCode(TaskID, ItemNo, msg["ReturnCode"]);//更新堆垛机错误编号
                }
                CraneErrWriteToPLC(msg["CraneNo"], int.Parse(msg["ReturnCode"]));
            }
        }
        /// <summary>
        /// 发送报文后，堆垛机发送接收确认。
        /// </summary>
        /// <param name="state"></param>
        private void ACK(object state)
        {
            Dictionary<string, string> msg = (Dictionary<string, string>)state;
            string SequenceNo = msg["SequenceNo"];
            DataRow dr = null;
            TaskDal dal = new TaskDal();
            if (dtCrane != null)
            {
                DataRow[] drs = dtCrane.Select(string.Format("SQUENCE_NO='{0}'", SequenceNo));
                if (drs.Length > 0)
                {
                    dr = drs[0];
                }
            }
            if (dr == null)
            {
                //根据流水号，获取资料
                DataTable dt = dal.CraneTaskIn(string.Format("DETAIL.SQUENCE_NO='{0}' AND DETAIL.CRANE_NO IS NOT NULL ", SequenceNo));
                if (dt.Rows.Count > 0)
                    dr = dt.Rows[0];
            }

            try
            {
                if (dr != null)
                {
                    string TaskType = dr["TASK_TYPE"].ToString();
                    string ItemNo = dr["ITEM_NO"].ToString();
                    BillDal bdal = new BillDal();
                    if (TaskType.Substring(1, 1) == "2" || (TaskType == "13" && ItemNo == "1") || (TaskType == "14" && ItemNo == "1" && dr["CRANE_NO"].ToString() != dr["NEW_CRANE_NO"].ToString()))
                    {

                        dal.UpdateTaskState(dr["TASK_ID"].ToString(), "1");
                        dal.UpdateTaskDetailCrane(dr["CELL_CODE"].ToString(), dr["STATION_NO"].ToString(), "1", dr["CRANE_NO"].ToString(), string.Format("TASK_ID='{0}' AND ITEM_NO={1}", dr["TASK_ID"], dr["ITEM_NO"]));
                        //更新BILL_MASTER 单据状态作业中
                        bdal.UpdateBillMasterStart(dr["BILL_NO"].ToString(), dr["PRODUCT_CODE"].ToString() == "0000" ? false : true);

                    }
                    else if (TaskType == "14" && ItemNo == "1" && dr["CRANE_NO"].ToString() == dr["NEW_CRANE_NO"].ToString())
                    {
                        //出库任务 更新任务状态:任务执行中
                        dal.UpdateTaskState(dr["TASK_ID"].ToString(), "1");
                        dal.UpdateTaskDetailCrane(dr["CELL_CODE"].ToString(), dr["NEWCELL_CODE"].ToString(), "1", dr["CRANE_NO"].ToString(), string.Format("TASK_ID='{0}' AND ITEM_NO={1}", dr["TASK_ID"], dr["ITEM_NO"]));
                        bdal.UpdateBillMasterStart(dr["BILL_NO"].ToString(), true);
                    }
                    else
                        dal.UpdateTaskDetailState(string.Format("TASK_ID='{0}' and ITEM_NO={1}", dr["TASK_ID"], dr["ITEM_NO"]), "1");

                    if (dtCrane != null)
                    {
                        DataRow[] drs = dtCrane.Select(string.Format("TASK_ID='{0}'", dr["TASK_ID"]));
                        if (drs.Length > 0)
                        {
                            drs[0].BeginEdit();
                            drs[0]["STATE"] = "1";
                            drs[0].EndEdit();
                            dtCrane.AcceptChanges();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        /// <summary>
        ///发送报文，堆垛机返回序列号错误，或Buffer已满
        /// </summary>
        /// <param name="state"></param>
        private void NCK(object state)
        {
            Dictionary<string, string> msg = (Dictionary<string, string>)state;
            SysStationDal dal = new SysStationDal();
            //序列号出错，重新发送报文
            if (msg["FaultIndicator"] == "1" ) 
            {
                if (msg["SequenceNo"] != "0001")
                {
                    //重置流水号为0；
                    
                    dal.ResetSQueNo();
                    //重新发送SYN
                    SendSYN();
                    if (lastCraneNo.Length > 0)
                    {
                        lock (dCraneState)
                        {
                            dCraneState[lastCraneNo] = "00000000";                            
                        }
                        SendTelegram(lastCraneNo, null);
                    }
                    else
                    {
                        CraneThreadStart();
                    }
                    NCK001 = 0;
                }
                else
                {
                    if (NCK001 == 100)
                    {
                        //重新发送SYN                        
                        dal.ResetSQueNo();
                        SendSYN();
                        if (lastCraneNo.Length > 0)
                        {
                            lock (dCraneState)
                            {
                                dCraneState[lastCraneNo] = "00000000";
                            }
                            SendTelegram(lastCraneNo, null);
                        }
                        else
                        {
                            CraneThreadStart();                           
                        }
                        NCK001 = 0;
                    }
                    else
                    {
                        CraneThreadStart();
                    }
                }          
            }
        }
        /// <summary>
        ///接收删除指令返回值
        /// </summary>
        /// <param name="state"></param>
        private void DEC(object state)
        {
            Dictionary<string, string> msg = (Dictionary<string, string>)state;
            SendACK(msg);

            if (msg["ReturnCode"] == "000") //序列号出错，重新发送报文
            {
                TaskDal dal = new TaskDal();
                DataTable dt = dal.CraneTaskIn(string.Format("DETAIL.CRANE_NO='{0}' AND ASSIGNMENT_ID='{1}'", msg["CraneNo"], msg["AssignmentID"]));
                DataRow dr = null;
                if (dt.Rows.Count > 0)
                    dr = dt.Rows[0];
                if (dr != null)
                {
                    #region 错误处理
                    
                    if (dr["ERR_CODE"].ToString() == "132") //入库，货位有货,重新分配货位
                    {
                        //不能直接再重新指定货位发送，必须先删除之前下的ARQ,才可再次发送，这个需手动作业
                        CellDal cdal = new CellDal();
                        cdal.UpdateCellErrFlag(dr["CELL_CODE"].ToString(), "货位有货，系统无记录");

                        //string[] strValue = dal.AssignNewCell(string.Format("TASK_ID='{0}'",dr["TASK_ID"].ToString()), dr["CRANE_NO"].ToString());//货位申请
                        //ProductStateDal StateDal = new ProductStateDal();
                        //StateDal.UpdateProductCellCode(strValue[0], strValue[1]); //更新Product_State 货位
                        
                        //SysStationDal sysdal = new SysStationDal();
                        //DataTable dtstation = sysdal.GetSationInfo(strValue[1], dr["TASK_TYPE"].ToString(), dr["ITEM_NO"].ToString());
                        //dal.UpdateTaskDetailCrane(dtstation.Rows[0]["STATION_NO"].ToString(), strValue[1], "1", dtstation.Rows[0]["CRANE_NO"].ToString(), string.Format("TASK_ID='{0}' AND ITEM_NO={1}", strValue[0], dr["ITEM_NO"].ToString()));//更新调度堆垛机的其实位置及目标地址。
                       
                        //dr.BeginEdit();
                        //dr["CELLSTATION"] = "30" + strValue[1] + "01";
                        //dr.EndEdit();
                        //SendTelegramARQ(dr, false);
                        //if (dtCrane != null)
                        //{
                        //    DataRow[] drs = dtCrane.Select(string.Format("ASSIGNMENT_ID='{0}'", msg["AssignmentID"]));
                        //    if (drs.Length > 0)
                        //        dtCrane.Rows.Remove(drs[0]);
                        //}
                    }
                    else if (dr["ERR_CODE"].ToString() == "135")//出库，货位无货，
                    {

                        string ErrMsg = "";
                        DataRow[] drMsgs = dtErrMesage.Select(string.Format("CODE='{0}'", dr["ERR_CODE"].ToString()));
                        if (drMsgs.Length > 0)
                            ErrMsg = drMsgs[0]["DESCRIPTION"].ToString();

                        string strBillNo = "";
                        string[] strMessage = new string[3];
                        strMessage[0] = "8";
                        strMessage[1] =dr["TASK_ID"].ToString();
                        strMessage[2] = "错误代码：" + dr["ERR_CODE"] + ",错误内容：" + ErrMsg;

                        DataTable dtProductInfo = dal.GetProductInfoByTaskID(dr["TASK_ID"].ToString());

                        try
                        {
                            while ((strBillNo = FormDialog.ShowDialog(strMessage, dtProductInfo)) != "")
                            {
                                BillDal bdal = new BillDal();
                                string strNewBillNo = strBillNo;

                                string strOutTaskID = bdal.CreateCancelBillOutTask(dr["TASK_ID"].ToString(), dr["BILL_NO"].ToString(), strNewBillNo);

                                DataTable dtOutTask = dal.CraneTaskOut(string.Format("TASK_ID='{0}'", strOutTaskID));

                                //WriteToProcess("CraneProcess", "CraneInRequest", dtOutTask);
                                InsertCraneQuene(dtOutTask);
                                SendTelegram(dtOutTask.Rows[0]["CRANE_NO"].ToString(), dtOutTask.Rows[0]);

                                CellDal cdal = new CellDal();
                                cdal.UpdateCellErrFlag(dr["CELL_CODE"].ToString(), "出库任务货位无货");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                }
                 
                #endregion
            }
        }
        #endregion

        #region 发送堆垛机报文
        /// <summary>
        /// blnValue=true 正常发送ARQ报文，如果目标地址有货，报警，并要重新指定新货位,blnValue=false
        /// </summary>
        /// <param name="dr"></param>
        /// <param name="blnValue"></param>
        private void SendTelegramARQ(DataRow dr, bool blnValue)
        {
            lock (ARQlocker)
            {
                THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();
                tgd.CraneNo = dr["CRANE_NO"].ToString();
                tgd.AssignmentID = dr["ASSIGNMENT_ID"].ToString();
                string FromStation = "";
                string ToStation = "";
                string UpdateType = "2";

                //如果已经发送过，就不再发送
                if (dCraneState[tgd.CraneNo] == tgd.AssignmentID)
                    return;

                string TaskType = dr["TASK_TYPE"].ToString();
                string ItemNo = dr["ITEM_NO"].ToString();

                if (TaskType.Substring(1, 1) == "4" && ItemNo == "1" && dr["CRANE_NO"].ToString() == dr["NEW_CRANE_NO"].ToString())
                {
                    tgd.StartPosition = dr["CELLSTATION"].ToString();
                    tgd.DestinationPosition = dr["NEW_TO_STATION"].ToString();
                    FromStation = dr["CELL_CODE"].ToString();
                    ToStation = dr["NEWCELL_CODE"].ToString();
                }
                else
                {
                    if (TaskType.Substring(1, 1) == "1" || (TaskType.Substring(1, 1) == "4" && ItemNo == "3") || TaskType.Substring(1, 1) == "3" && ItemNo == "4") //入库
                    {
                        tgd.StartPosition = dr["CRANESTATION"].ToString();
                        tgd.DestinationPosition = dr["CELLSTATION"].ToString();
                        UpdateType = "1";
                    }
                    else //出库
                    {
                        tgd.StartPosition = dr["CELLSTATION"].ToString();
                        tgd.DestinationPosition = dr["CRANESTATION"].ToString();
                        FromStation = dr["CELL_CODE"].ToString();
                        ToStation = dr["STATION_NO"].ToString();
                    }
                }

                THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
                string QuenceNo = GetNextSQuenceNo();
                string str = tf.DataFraming("1" + QuenceNo, tgd, tf.TelegramARQ);
                WriteToService("Crane", "ARQ", str);

                DataRow[] drs = dtCrane.Select(string.Format("TASK_ID='{0}'", dr["TASK_ID"]));
                if (drs.Length > 0)
                {
                    drs[0].BeginEdit();
                    drs[0]["SQUENCE_NO"] = QuenceNo;
                    drs[0].EndEdit();
                    dtCrane.AcceptChanges();

                    dr.BeginEdit();
                    dr["SQUENCE_NO"] = QuenceNo;
                    dr.EndEdit();
                }

                lock (dCraneState)
                {
                    dCraneState[dr["CRANE_NO"].ToString()] = tgd.AssignmentID;
                }
                lastCraneNo = dr["CRANE_NO"].ToString();
                Logger.Info("堆垛机" + dr["CRANE_NO"].ToString() + "任务：" + tgd.AssignmentID + "开始执行");

                //更新发送报文。
                TaskDal dal = new TaskDal();
                if (UpdateType == "1")
                    dal.UpdateCraneQuenceNo(dr["TASK_ID"].ToString(), QuenceNo, ItemNo);
                else
                    dal.UpdateCraneQuenceNo(dr["TASK_ID"].ToString(), ItemNo, tgd.CraneNo, QuenceNo, tgd.StartPosition, tgd.DestinationPosition);
            }
        }
        //请求堆垛机状态
        private void SendTelegramCRQ(string CraneNo)
        {
            THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();
            tgd.CraneNo = CraneNo;
            THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
            string QuenceNo = GetNextSQuenceNo();
            string str = tf.DataFraming("1" + QuenceNo, tgd, tf.TelegramCRQ);
            WriteToService("Crane", "CRQ", str);
            //记录发送的CRQ报文，预防堆垛机返回错误序列号的NCK。
            if (dtSendCRQ == null)
            {
                dtSendCRQ = new DataTable();
                dtSendCRQ.Columns.Add("CRANE_NO", Type.GetType("System.String"));
                dtSendCRQ.Columns.Add("SQUENCE_NO", Type.GetType("System.String"));
            }

            DataRow dr = dtSendCRQ.NewRow();
            dr.BeginEdit();
            dr["CRANE_NO"] = CraneNo;
            dr["SQUENCE_NO"] = QuenceNo;
            dr.EndEdit();
            dtSendCRQ.Rows.Add(dr);
            dtSendCRQ.AcceptChanges();
        }
        //请求堆垛机状态
        private void SendTelegramCRQ()
        {
            for (int i = 1; i < 7; i++)
            {
                string CraneNo = i.ToString().PadLeft(2, '0');

                THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();
                tgd.CraneNo = CraneNo;
                THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
                string QuenceNo = GetNextSQuenceNo();
                string str = tf.DataFraming("1" + QuenceNo, tgd, tf.TelegramCRQ);
                WriteToService("Crane", "CRQ", str);
                //记录发送的CRQ报文，预防堆垛机返回错误序列号的NCK。
                if (dtSendCRQ == null)
                {
                    dtSendCRQ = new DataTable();
                    dtSendCRQ.Columns.Add("CRANE_NO", Type.GetType("System.String"));
                    dtSendCRQ.Columns.Add("SQUENCE_NO", Type.GetType("System.String"));
                }

                DataRow dr = dtSendCRQ.NewRow();
                dr.BeginEdit();
                dr["CRANE_NO"] = CraneNo;
                dr["SQUENCE_NO"] = QuenceNo;
                dr.EndEdit();
                dtSendCRQ.Rows.Add(dr);
                dtSendCRQ.AcceptChanges();
            }
        }
        /// <summary>
        /// 删除指令
        /// </summary>
        /// <param name="dr"></param>
        private void SendTelegramDER(DataRow dr)
        {
            THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();
            tgd.CraneNo = dr["CRANE_NO"].ToString();
            tgd.AssignmentID = dr["ASSIGNMENT_ID"].ToString();
            
            THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
            string QuenceNo = GetNextSQuenceNo();
            string str = tf.DataFraming("1" + QuenceNo, tgd, tf.TelegramDER);
            WriteToService("Crane", "DER", str);
        }

        private string GetNextSQuenceNo()
        {
            SysStationDal dal = new SysStationDal();
            return dal.GetTaskNo("S");
        }
        private void SendACK(Dictionary<string, string> msg)
        {
            if (msg["ConfirmFlag"] == "1")
            {
                THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();
                tgd.SequenceNo = msg["SeqNo"];
                THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
                string str = tf.DataFraming("00000", tgd, tf.TelegramACK);
                //string str = "<00000CRAN30THOK01ACK0" + msg["SeqNo"] + "00>";
                WriteToService("Crane", "ACK", str);
            }
        }

        private void SendSYN()
        {
            THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();

            THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
            string str = tf.DataFraming("00000", tgd, tf.TelegramSYN);
            WriteToService("Crane", "SYN", str);
            //WriteToService("Crane", "DUM", "<00000CRAN30THOK01SYN0000000>");
        }
        private void SendDUM()
        {
            THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();

            THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
            string str = tf.DataFraming("00000", tgd, tf.TelegramDUM);
            WriteToService("Crane", "DUM", str);
            //WriteToService("Crane", "DUM", "<00000CRAN30THOK01DUM0000000>");
        }
        private void SendDUA()
        {
            THOK.CRANE.TelegramData tgd = new CRANE.TelegramData();
            THOK.CRANE.TelegramFraming tf = new CRANE.TelegramFraming();
            string str = tf.DataFraming("00000", tgd, tf.TelegramDUA);
            WriteToService("Crane", "DUA", str);
        }
        /// <summary>
        /// 堆垛机返回错误号，写入PLC
        /// </summary>
        /// <param name="CraneNo"></param>
        /// <param name="ErrCode"></param>
        private void CraneErrWriteToPLC(string CraneNo, int ErrCode)
        {
            WriteToService("StockPLC_01", "01_2_C" + CraneNo, ErrCode);
            WriteToService("StockPLC_02", "02_2_C" + CraneNo, ErrCode);
        }
        /// <summary>
        /// 出库到站台下任务给电控
        /// </summary>
        /// <param name="TaskNo"></param>
        /// <param name="ToStation"></param>
        /// <param name="ProductType"></param>
        /// <param name="Barcode"></param>
        /// <param name="PalletCode"></param>
        /// <param name="ServiceName"></param>
        /// <param name="ItemName2"></param>
        private void WriteToPLC(int TaskNo, int ToStation, int ProductType, string Barcode, string PalletCode, string ServiceName, string ItemName2)
        {
            int[] WriteValue = new int[3];
            WriteValue[0] = TaskNo;
            WriteValue[1] = ToStation;
            WriteValue[2] = ProductType;

            byte[] b = new byte[290];
            for (int k = 0; k < 290; k++)
                b[k] = 32;
            Common.ConvertStringChar.stringToByte(Barcode, 200).CopyTo(b, 0);
            Common.ConvertStringChar.stringToByte(PalletCode, 90).CopyTo(b, 200);
            //到达出库站台，再下任务给PLC

            Logger.Info("下达任务给PLC01" + WriteValue[0]);
            WriteToService(ServiceName, ItemName2 + "_1", WriteValue);
            WriteToService(ServiceName, ItemName2 + "_2", b);
            WriteToService(ServiceName, ItemName2 + "_3", 1);
            Logger.Info("下达任务给PLC01" + WriteValue[0] + WriteValue[1] + WriteValue[2]);
        }
        #endregion
    }
}
