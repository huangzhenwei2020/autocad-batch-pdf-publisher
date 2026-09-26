using System;
using System.Collections.Generic;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 模型编辑的撤销/重做：按"整份模型快照"记录。
    ///
    /// 为什么用快照而不是命令模式：模型很小（几十 KB 的 JSON），
    /// 快照实现简单、绝不会出现"撤销后状态不一致"，对 P1.5 这种交互编辑足够；
    /// 等模型大到几万构件再换成差量记录。
    /// </summary>
    public sealed class ModelEditHistory
    {
        private const int MaxStates = 64;
        private readonly List<string> _states = new List<string>();
        private int _index = -1;

        public int Count { get { return _states.Count; } }
        public bool CanUndo { get { return _index > 0; } }
        public bool CanRedo { get { return _index >= 0 && _index < _states.Count - 1; } }

        /// <summary>清空历史并记录初始状态。</summary>
        public void Reset(BuildingModelDocument model)
        {
            _states.Clear();
            _index = -1;
            Push(model);
        }

        /// <summary>记录一次改动之后的状态（会丢弃"重做"分支）。</summary>
        public void Push(BuildingModelDocument model)
        {
            if (model == null) return;
            var json = BuildingModelJson.ToJson(model);
            if (_index >= 0 && _index < _states.Count && string.Equals(_states[_index], json, StringComparison.Ordinal)) return;
            if (_index < _states.Count - 1) _states.RemoveRange(_index + 1, _states.Count - _index - 1);
            _states.Add(json);
            if (_states.Count > MaxStates) _states.RemoveAt(0);
            _index = _states.Count - 1;
        }

        public BuildingModelDocument Undo(BuildingModelDocument current)
        {
            if (!CanUndo) return current;
            _index--;
            return BuildingModelJson.FromJson(_states[_index]);
        }

        public BuildingModelDocument Redo(BuildingModelDocument current)
        {
            if (!CanRedo) return current;
            _index++;
            return BuildingModelJson.FromJson(_states[_index]);
        }

        /// <summary>当前状态在栈里的位置（从 1 开始显示给用户）。</summary>
        public int Position { get { return _index + 1; } }
    }
}
