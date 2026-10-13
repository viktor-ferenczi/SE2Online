using System;

namespace Y51_SE2Online.SyncEngine;

internal readonly record struct Segment(SegmentKind Kind, int Size, int Param, Type? Type);
