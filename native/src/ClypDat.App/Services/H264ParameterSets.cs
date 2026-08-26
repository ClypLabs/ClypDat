namespace ClypDat.App.Services;

// Bit-level read/rewrite of H.264 SPS and PPS NAL units.
//
// This exists for ClipCorruptionRepairService. Clips written by builds between
// 1.2.1 and 1.3.0 can carry one encoder's parameter sets over another encoder's
// slices, and because the replay encoder runs with AV_CODEC_FLAG_GLOBAL_HEADER
// the slices themselves contain no SPS/PPS to recover from. The correct sets
// have to be reconstructed, which means parsing what is there, changing the few
// fields that differ between encoders, and writing it back out.
//
// Only the fields ahead of the VUI are modelled. Everything from
// vui_parameters_present_flag onward is carried as an opaque bit tail, so the
// colour/timing information survives a rewrite untouched.
internal static class H264ParameterSets
{
    private static readonly int[] HighProfiles = { 100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135 };

    internal sealed class SequenceParameterSet
    {
        public int ProfileIdc, ConstraintFlags, LevelIdc, SeqParameterSetId;
        public int ChromaFormatIdc = 1, SeparateColourPlaneFlag, BitDepthLumaMinus8, BitDepthChromaMinus8;
        public int QpprimeYZeroTransformBypassFlag, SeqScalingMatrixPresentFlag;
        public int Log2MaxFrameNumMinus4, PicOrderCntType, Log2MaxPicOrderCntLsbMinus4;
        public int DeltaPicOrderAlwaysZeroFlag, OffsetForNonRefPic, OffsetForTopToBottomField;
        public int[] OffsetForRefFrame = Array.Empty<int>();
        public int MaxNumRefFrames, GapsInFrameNumValueAllowedFlag;
        public int PicWidthInMbsMinus1, PicHeightInMapUnitsMinus1;
        public int FrameMbsOnlyFlag = 1, MbAdaptiveFrameFieldFlag, Direct8x8InferenceFlag;
        public int FrameCroppingFlag;
        public int[] Crop = new int[4];
        public bool[] Tail = Array.Empty<bool>();

        public SequenceParameterSet Clone() => (SequenceParameterSet)MemberwiseClone();
    }

    internal sealed class PictureParameterSet
    {
        public int PicParameterSetId, SeqParameterSetId, EntropyCodingModeFlag;
        public int BottomFieldPicOrderInFramePresentFlag, NumSliceGroupsMinus1;
        public int NumRefIdxL0DefaultActiveMinus1, NumRefIdxL1DefaultActiveMinus1;
        public int WeightedPredFlag, WeightedBipredIdc;
        public int PicInitQpMinus26, PicInitQsMinus26, ChromaQpIndexOffset;
        public int DeblockingFilterControlPresentFlag, ConstrainedIntraPredFlag, RedundantPicCntPresentFlag;
        public bool HasExtension;
        public int Transform8x8ModeFlag, SecondChromaQpIndexOffset;

        public PictureParameterSet Clone() => (PictureParameterSet)MemberwiseClone();
    }

    // 0x000003 is the emulation prevention escape: a byte-stream NAL payload can
    // never contain three-byte start-code-like runs, so encoders inject a 0x03
    // that has to come back out before the RBSP can be read as bits.
    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 3)
            {
                output.Add(0);
                output.Add(0);
                i += 2;
                continue;
            }
            output.Add(data[i]);
        }
        return output.ToArray();
    }

    private static byte[] Escape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + 8);
        var zeros = 0;
        foreach (var value in data)
        {
            if (zeros >= 2 && value <= 3) { output.Add(3); zeros = 0; }
            output.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return output.ToArray();
    }

    private sealed class BitReader(byte[] data)
    {
        private int _position;
        public int BitLength => data.Length * 8;
        public int Position => _position;

        public int U(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if (_position >= BitLength) throw new InvalidDataException("H.264 parameter set ended early.");
                value = (value << 1) | ((data[_position >> 3] >> (7 - (_position & 7))) & 1);
                _position++;
            }
            return value;
        }

        public int Ue()
        {
            var leadingZeros = 0;
            while (U(1) == 0)
            {
                if (++leadingZeros > 31) throw new InvalidDataException("H.264 exp-Golomb code overran.");
            }
            return (1 << leadingZeros) - 1 + (leadingZeros == 0 ? 0 : U(leadingZeros));
        }

        public int Se()
        {
            var value = Ue();
            return (value & 1) != 0 ? (value + 1) / 2 : -(value / 2);
        }
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = new(512);

        public void U(int count, int value)
        {
            for (var i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }

        public void Ue(int value)
        {
            var shifted = value + 1;
            var width = 0;
            while (shifted >> (width + 1) != 0) width++;
            U(width, 0);
            U(width + 1, shifted);
        }

        public void Se(int value) => Ue(value > 0 ? 2 * value - 1 : -2 * value);

        public void Append(bool[] bits) => _bits.AddRange(bits);

        // The caller supplies its own trailing bits when a tail was carried, so
        // this only pads to a byte boundary.
        public byte[] ToBytes()
        {
            var bits = new List<bool>(_bits);
            while (bits.Count % 8 != 0) bits.Add(false);
            var output = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
            {
                if (bits[i]) output[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
            return output;
        }
    }

    internal static SequenceParameterSet ParseSps(ReadOnlySpan<byte> nal)
    {
        var reader = new BitReader(Unescape(nal[1..]));
        var sps = new SequenceParameterSet
        {
            ProfileIdc = reader.U(8),
            ConstraintFlags = reader.U(8),
            LevelIdc = reader.U(8),
        };
        sps.SeqParameterSetId = reader.Ue();
        if (Array.IndexOf(HighProfiles, sps.ProfileIdc) >= 0)
        {
            sps.ChromaFormatIdc = reader.Ue();
            if (sps.ChromaFormatIdc == 3) sps.SeparateColourPlaneFlag = reader.U(1);
            sps.BitDepthLumaMinus8 = reader.Ue();
            sps.BitDepthChromaMinus8 = reader.Ue();
            sps.QpprimeYZeroTransformBypassFlag = reader.U(1);
            sps.SeqScalingMatrixPresentFlag = reader.U(1);
            // Custom scaling lists would have to be re-emitted verbatim; the
            // replay encoders never set them, so refuse rather than corrupt.
            if (sps.SeqScalingMatrixPresentFlag != 0) throw new InvalidDataException("SPS carries scaling matrices.");
        }
        sps.Log2MaxFrameNumMinus4 = reader.Ue();
        sps.PicOrderCntType = reader.Ue();
        if (sps.PicOrderCntType == 0)
        {
            sps.Log2MaxPicOrderCntLsbMinus4 = reader.Ue();
        }
        else if (sps.PicOrderCntType == 1)
        {
            sps.DeltaPicOrderAlwaysZeroFlag = reader.U(1);
            sps.OffsetForNonRefPic = reader.Se();
            sps.OffsetForTopToBottomField = reader.Se();
            var count = reader.Ue();
            sps.OffsetForRefFrame = new int[count];
            for (var i = 0; i < count; i++) sps.OffsetForRefFrame[i] = reader.Se();
        }
        sps.MaxNumRefFrames = reader.Ue();
        sps.GapsInFrameNumValueAllowedFlag = reader.U(1);
        sps.PicWidthInMbsMinus1 = reader.Ue();
        sps.PicHeightInMapUnitsMinus1 = reader.Ue();
        sps.FrameMbsOnlyFlag = reader.U(1);
        if (sps.FrameMbsOnlyFlag == 0) sps.MbAdaptiveFrameFieldFlag = reader.U(1);
        sps.Direct8x8InferenceFlag = reader.U(1);
        sps.FrameCroppingFlag = reader.U(1);
        if (sps.FrameCroppingFlag != 0)
        {
            for (var i = 0; i < 4; i++) sps.Crop[i] = reader.Ue();
        }

        var tail = new List<bool>();
        while (reader.Position < reader.BitLength) tail.Add(reader.U(1) != 0);
        sps.Tail = tail.ToArray();
        return sps;
    }

    internal static byte[] WriteSps(SequenceParameterSet sps)
    {
        var writer = new BitWriter();
        writer.U(8, sps.ProfileIdc);
        writer.U(8, sps.ConstraintFlags);
        writer.U(8, sps.LevelIdc);
        writer.Ue(sps.SeqParameterSetId);
        if (Array.IndexOf(HighProfiles, sps.ProfileIdc) >= 0)
        {
            writer.Ue(sps.ChromaFormatIdc);
            if (sps.ChromaFormatIdc == 3) writer.U(1, sps.SeparateColourPlaneFlag);
            writer.Ue(sps.BitDepthLumaMinus8);
            writer.Ue(sps.BitDepthChromaMinus8);
            writer.U(1, sps.QpprimeYZeroTransformBypassFlag);
            writer.U(1, sps.SeqScalingMatrixPresentFlag);
        }
        writer.Ue(sps.Log2MaxFrameNumMinus4);
        writer.Ue(sps.PicOrderCntType);
        if (sps.PicOrderCntType == 0)
        {
            writer.Ue(sps.Log2MaxPicOrderCntLsbMinus4);
        }
        else if (sps.PicOrderCntType == 1)
        {
            writer.U(1, sps.DeltaPicOrderAlwaysZeroFlag);
            writer.Se(sps.OffsetForNonRefPic);
            writer.Se(sps.OffsetForTopToBottomField);
            writer.Ue(sps.OffsetForRefFrame.Length);
            foreach (var offset in sps.OffsetForRefFrame) writer.Se(offset);
        }
        writer.Ue(sps.MaxNumRefFrames);
        writer.U(1, sps.GapsInFrameNumValueAllowedFlag);
        writer.Ue(sps.PicWidthInMbsMinus1);
        writer.Ue(sps.PicHeightInMapUnitsMinus1);
        writer.U(1, sps.FrameMbsOnlyFlag);
        if (sps.FrameMbsOnlyFlag == 0) writer.U(1, sps.MbAdaptiveFrameFieldFlag);
        writer.U(1, sps.Direct8x8InferenceFlag);
        writer.U(1, sps.FrameCroppingFlag);
        if (sps.FrameCroppingFlag != 0)
        {
            foreach (var crop in sps.Crop) writer.Ue(crop);
        }
        // The tail already carries vui_parameters_present_flag, the VUI itself,
        // the rbsp_stop_bit and its alignment.
        writer.Append(sps.Tail);

        var payload = Escape(writer.ToBytes());
        var nal = new byte[payload.Length + 1];
        nal[0] = 0x67;
        payload.CopyTo(nal, 1);
        return nal;
    }

    internal static PictureParameterSet ParsePps(ReadOnlySpan<byte> nal)
    {
        var rbsp = Unescape(nal[1..]);
        var reader = new BitReader(rbsp);
        var pps = new PictureParameterSet
        {
            PicParameterSetId = reader.Ue(),
            SeqParameterSetId = reader.Ue(),
            EntropyCodingModeFlag = reader.U(1),
            BottomFieldPicOrderInFramePresentFlag = reader.U(1),
            NumSliceGroupsMinus1 = reader.Ue(),
        };
        if (pps.NumSliceGroupsMinus1 != 0) throw new InvalidDataException("PPS uses slice groups.");
        pps.NumRefIdxL0DefaultActiveMinus1 = reader.Ue();
        pps.NumRefIdxL1DefaultActiveMinus1 = reader.Ue();
        pps.WeightedPredFlag = reader.U(1);
        pps.WeightedBipredIdc = reader.U(2);
        pps.PicInitQpMinus26 = reader.Se();
        pps.PicInitQsMinus26 = reader.Se();
        pps.ChromaQpIndexOffset = reader.Se();
        pps.DeblockingFilterControlPresentFlag = reader.U(1);
        pps.ConstrainedIntraPredFlag = reader.U(1);
        pps.RedundantPicCntPresentFlag = reader.U(1);

        // more_rbsp_data(): the RBSP ends with a single set stop bit followed by
        // zero padding to the byte boundary, so the last set bit in the whole
        // buffer IS the stop bit. Anything still unread before it is the
        // High-profile extension.
        //
        // Scanning forward for the first set bit instead - which an earlier
        // version did - cannot tell the stop bit from the extension's first
        // flag, so a PPS with no extension was read as having one and the
        // parse ran off the end of the buffer.
        if (reader.Position < LastSetBitIndex(rbsp))
        {
            pps.HasExtension = true;
            pps.Transform8x8ModeFlag = reader.U(1);
            if (reader.U(1) != 0) throw new InvalidDataException("PPS carries scaling matrices.");
            pps.SecondChromaQpIndexOffset = reader.Se();
        }
        return pps;
    }

    /// <summary>Bit index of the rbsp_stop_bit, or -1 when the buffer is all zero.</summary>
    private static int LastSetBitIndex(byte[] rbsp)
    {
        for (var i = rbsp.Length - 1; i >= 0; i--)
        {
            if (rbsp[i] == 0) continue;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((rbsp[i] & (1 << bit)) != 0) return i * 8 + (7 - bit);
            }
        }
        return -1;
    }

    internal static byte[] WritePps(PictureParameterSet pps)
    {
        var writer = new BitWriter();
        writer.Ue(pps.PicParameterSetId);
        writer.Ue(pps.SeqParameterSetId);
        writer.U(1, pps.EntropyCodingModeFlag);
        writer.U(1, pps.BottomFieldPicOrderInFramePresentFlag);
        writer.Ue(pps.NumSliceGroupsMinus1);
        writer.Ue(pps.NumRefIdxL0DefaultActiveMinus1);
        writer.Ue(pps.NumRefIdxL1DefaultActiveMinus1);
        writer.U(1, pps.WeightedPredFlag);
        writer.U(2, pps.WeightedBipredIdc);
        writer.Se(pps.PicInitQpMinus26);
        writer.Se(pps.PicInitQsMinus26);
        writer.Se(pps.ChromaQpIndexOffset);
        writer.U(1, pps.DeblockingFilterControlPresentFlag);
        writer.U(1, pps.ConstrainedIntraPredFlag);
        writer.U(1, pps.RedundantPicCntPresentFlag);
        if (pps.HasExtension)
        {
            writer.U(1, pps.Transform8x8ModeFlag);
            writer.U(1, 0);
            writer.Se(pps.SecondChromaQpIndexOffset);
        }
        writer.U(1, 1);

        var payload = Escape(writer.ToBytes());
        var nal = new byte[payload.Length + 1];
        nal[0] = 0x68;
        payload.CopyTo(nal, 1);
        return nal;
    }

    /// <summary>Offsets of every NAL payload in an Annex-B byte stream.</summary>
    internal static List<(int Start, int Length)> SplitAnnexB(byte[] data)
    {
        var starts = new List<(int Payload, int StartCode)>();
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;
            starts.Add((i + 3, i > 0 && data[i - 1] == 0 ? 4 : 3));
            i += 2;
        }

        var result = new List<(int, int)>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Payload - starts[i + 1].StartCode : data.Length;
            result.Add((starts[i].Payload, end - starts[i].Payload));
        }
        return result;
    }
}
