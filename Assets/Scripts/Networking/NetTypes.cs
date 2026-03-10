using UnityEngine;

namespace Networking
{
    //Ϊ��ôҪʹ�ýṹ�壬������class������GC
    [System.Flags]
    public enum InputButtons : byte
    {
        None   = 0,
        Jump   = 1 << 0,
        Attack = 1 << 1,
        Dodge  = 1 << 2,
        Skill1 = 1 << 3,
        Skill2 = 1 << 4,
        Lock   = 1 << 5,
    }

    /// <summary>   
    /// Client -> Server��ÿ tick �������루��ͼ��
    /// </summary>
    [System.Serializable]
    public struct PlayerInputCmd
    {
        public int tick;

        /// <summary>����ռ� XZ���Ѿ�����������</summary>
        public Vector2 moveDirXZ;

        /// <summary>0~1��ҡ��ǿ��/�Ƿ���·��</summary>
        public float moveAmount;

        /// <summary>����ռ䳯������ת��/����/���ܳ���</summary>
        public Vector3 aimDir;

        public InputButtons buttons;
    }

    /// <summary>
    /// Server -> Client��Ȩ�����գ������
    /// </summary>
    [System.Serializable]
    public struct NetSnapshot
    {
        public int tick;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
    }

    /// <summary>
    /// ����Ԥ�⻺�棨���ڻع���
    /// </summary>
    [System.Serializable]
    public struct MotorState
    {
        public int tick;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
    }
}
