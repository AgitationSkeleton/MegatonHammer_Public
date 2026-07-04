using OpenTK.Mathematics;

namespace MegatonHammer.Editor;

public struct Plane3D
{
    public Vector3 Normal;
    public float   Distance;

    public Plane3D(Vector3 normal, float distance)
    {
        Normal   = normal;
        Distance = distance;
    }

    public float Evaluate(Vector3 p) => Vector3.Dot(Normal, p) - Distance;

    public bool Contains(Vector3 p, float epsilon = 0.5f) => Evaluate(p) <= epsilon;
}
