using System.Collections.Generic;
using UnityEngine;

namespace Networking
{
    /// <summary>
    /// 快照插值：用于非权威对象（远端玩家/敌人）平滑显示
    /// </summary>
    public class SnapshotInterpolator
    {
        readonly List<NetSnapshot> _buffer = new List<NetSnapshot>(64);
        readonly int _maxBuffer;

        public int LatestTick { get; private set; }

        public SnapshotInterpolator(int maxBuffer = 64)
        {
            _maxBuffer = Mathf.Max(8, maxBuffer);
        }

        public void Clear()
        {
            _buffer.Clear();
            LatestTick = 0;
        }

        // ? 完全保留：签名不变
        public void Add(NetSnapshot s)
        {
            LatestTick = Mathf.Max(LatestTick, s.tick);

            // 保持按 tick 有序插入（通常是递增到达）
            if (_buffer.Count == 0 || s.tick > _buffer[_buffer.Count - 1].tick)
            {
                _buffer.Add(s);
            }
            else
            {
                // 乱序/重复：插入或覆盖
                int idx = _buffer.FindIndex(x => x.tick >= s.tick);
                if (idx >= 0 && _buffer[idx].tick == s.tick) _buffer[idx] = s;
                else if (idx >= 0) _buffer.Insert(idx, s);
                else _buffer.Add(s);
            }

            // 裁剪旧数据
            while (_buffer.Count > _maxBuffer)
                _buffer.RemoveAt(0);
        }

        // ? 保留你的旧接口：不改任何现有调用
        public bool TrySample(int renderTick, out NetSnapshot sampled)
        {
            // 旧接口只是包装：renderTick 是整数 -> 没有真正插值，只会跳
            return TrySample((float)renderTick, out sampled);
        }

        // ? 新增：真正用于插值的接口（renderTickF = tick + alpha）
        public bool TrySample(float renderTickF, out NetSnapshot sampled)
        {
            sampled = default;
            if (_buffer.Count == 0) return false;

            // 过新：用最新
            if (renderTickF >= _buffer[_buffer.Count - 1].tick)
            {
                sampled = _buffer[_buffer.Count - 1];
                return true;
            }

            // 过旧：用最旧
            if (renderTickF <= _buffer[0].tick)
            {
                sampled = _buffer[0];
                return true;
            }

            // 找 [a,b] 使得 a.tick <= renderTickF <= b.tick
            for (int i = 0; i < _buffer.Count - 1; i++)
            {
                var a = _buffer[i];
                var b = _buffer[i + 1];

                if (a.tick <= renderTickF && renderTickF <= b.tick)
                {
                    float denom = (b.tick - a.tick);
                    float t = denom <= 1e-5f ? 0f : (renderTickF - a.tick) / denom;
                    t = Mathf.Clamp01(t);

                    sampled.tick = Mathf.FloorToInt(renderTickF);
                    sampled.position = Vector3.Lerp(a.position, b.position, t);
                    sampled.rotation = Quaternion.Slerp(a.rotation, b.rotation, t);
                    sampled.velocity = Vector3.Lerp(a.velocity, b.velocity, t);
                    return true;
                }
            }

            sampled = _buffer[_buffer.Count - 1];
            return true;
        }
    }
}
