using System;

namespace ScrewMachine.MissionControl.MainDirector.Actions
{
    /// <summary>
    /// 运动学与坐标系转换助手 (Kinematics Helper)
    /// </summary>
    public static class KinematicsHelper
    {
        /// <summary>
        /// 2D 仿射变换：将产品的局部坐标，转换为机器的绝对世界坐标
        /// </summary>
        /// <param name="localX">点位表中的理想 X 坐标</param>
        /// <param name="localY">点位表中的理想 Y 坐标</param>
        /// <param name="offsetX">视觉 Mark 点偏差 X</param>
        /// <param name="offsetY">视觉 Mark 点偏差 Y</param>
        /// <param name="angleDeg">视觉 Mark 点旋转角度 (度)</param>
        /// <param name="baseX">工站机械原点 X</param>
        /// <param name="baseY">工站机械原点 Y</param>
        /// <returns>计算后的绝对世界坐标 (WorldX, WorldY)</returns>
        public static (double WorldX, double WorldY) TransformToWorld(
            double localX, double localY,
            double offsetX, double offsetY, double angleDeg,
            double baseX, double baseY)
        {
            // 1. 角度转弧度
            double rad = angleDeg * Math.PI / 180.0;
            double cosA = Math.Cos(rad);
            double sinA = Math.Sin(rad);

            // 2. 原地旋转 (基于产品局部坐标系原点 0,0)
            double rotX = localX * cosA - localY * sinA;
            double rotY = localX * sinA + localY * cosA;

            // 3. 叠加视觉偏差 (Translation) 与 机械工站基准偏差 (Base Offset)
            double worldX = rotX + offsetX + baseX;
            double worldY = rotY + offsetY + baseY;

            return (worldX, worldY);
        }
    }
}