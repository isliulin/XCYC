﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace WMSWEBSERVICE.Tables
{
    /// <summary>
    /// 烟叶等级
    /// </summary>
    public class CMD_PRODUCT_GRADE
    {
        [DBField("id", EnumDBFieldUsage.PrimaryKey, "烟叶等级ID", "VARCHAR", "12", "必传")]
        public string id { get; set; }
        [DBField("code", EnumDBFieldUsage.None, "烟叶等级代码", "VARCHAR", "4", "必传")]
        public string code { get; set; }
        [DBField("name", EnumDBFieldUsage.None, "烟叶等级名称", "VARCHAR", "50", "必传")]
        public string name { get; set; }
        [DBField("big_clas_id", EnumDBFieldUsage.None, "英文代号", "VARCHAR", "50", "必传")]
        public string big_clas_id { get; set; }
        [DBField("leaf_type_id", EnumDBFieldUsage.None, "类型编码", "VARCHAR", "12", "必传")]
        public string leaf_type_id { get; set; }
        [DBField("leaf_color_id", EnumDBFieldUsage.None, "烟叶颜色", "VARCHAR", "12", "必传")]
        public string leaf_color_id { get; set; }
        [DBField("leaf_part_id", EnumDBFieldUsage.None, "烟叶部位", "VARCHAR", "12", "必传")]
        public string leaf_part_id { get; set; }
        [DBField("status", EnumDBFieldUsage.None, "状态", "VARCHAR", "2", "必传")]
        public string status { get; set; }
        [DBField("creator", EnumDBFieldUsage.None, "创建人", "VARCHAR", "30", "")]
        public string creator { get; set; }
        [DBField("createtime", EnumDBFieldUsage.None, "创建日期", "TIMESTAMP", "19", "")]
        public string createtime { get; set; }
        [DBField("modifier", EnumDBFieldUsage.None, "修改者", "VARCHAR", "30", "")]
        public string modifier { get; set; }
        [DBField("modifytime", EnumDBFieldUsage.None, "修改日期", "TIMESTAMP", "19", "")]
        public string modifytime { get; set; }
    }
}