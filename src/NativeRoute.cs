using System;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    public sealed class NativeRoute : IRouteGraph<RailTrack>
    {
        public static readonly NativeRoute Instance = new NativeRoute();
        public bool Valid(RailTrack track) { return track != null && track.GetKinkedPointSet() != null; }
        public double Length(RailTrack track) { return Valid(track) ? track.GetKinkedPointSet().span : 0; }
        public bool Next(RailTrack track, int direction, out RailTrack next, out int nextDirection)
        {
            var branch = direction > 0 ? track.GetOutBranch() : track.GetInBranch();
            next = branch == null ? null : branch.track;
            nextDirection = branch != null && branch.first ? 1 : -1;
            return Valid(next);
        }

        public static bool LeadingPosition(TrainCar car, int direction, out TrackPosition<RailTrack> result)
        {
            result = default(TrackPosition<RailTrack>);
            Bogie bogie = direction > 0 ? car.FrontBogie : car.RearBogie;
            if (bogie == null || !bogie.fullyInitialized || bogie.HasDerailed || bogie.traveller == null || bogie.track == null) return false;
            var tangent = bogie.traveller.curPoint.forward;
            int trackDirection = Vector3.Dot(tangent, car.transform.forward) >= 0 ? direction : -direction;
            var start = new TrackPosition<RailTrack>(bogie.track, bogie.traveller.Span, trackDirection);
            float bogieZ = car.transform.InverseTransformPoint(bogie.transform.position).z;
            var bounds = car.Bounds;
            double overhang = Math.Max(0, (bounds.center.z + direction * bounds.extents.z - bogieZ) * direction);
            // Never extrapolate beyond an unconnected buffer stop.
            RouteLogic.Advance(Instance, start, overhang, out result);
            return Instance.Valid(result.Track);
        }

        public static bool Project(RailTrack track, Vector3 absolute, out double span, out Vector3 tangent, out float distance)
        {
            span = 0; tangent = Vector3.forward; distance = float.PositiveInfinity;
            if (track == null) return false;
            var pointSet = track.GetKinkedPointSet();
            if (pointSet == null || pointSet.points == null || pointSet.points.Length < 2) return false;
            var points = pointSet.points;
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 a = (Vector3)points[i].position, b = (Vector3)points[i + 1].position;
                Vector3 delta = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(absolute - a, delta) / Mathf.Max(delta.sqrMagnitude, 0.00001f));
                float sq = (absolute - a - delta * t).sqrMagnitude;
                if (sq >= distance) continue;
                distance = sq; span = points[i].span + t * points[i].spanToNextPoint;
                tangent = delta.normalized;
            }
            distance = Mathf.Sqrt(distance);
            return !float.IsInfinity(distance);
        }
    }
}
