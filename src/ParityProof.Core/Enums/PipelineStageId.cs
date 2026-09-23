namespace ParityProof.Core.Enums;

public enum PipelineStageId
{
    Discovery,
    Hashing,
    DestinationMatching,
    DuplicateAnalysis,
    TransferWrite,
    PostTransferVerify
}
