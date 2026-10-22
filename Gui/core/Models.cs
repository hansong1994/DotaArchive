using System;
using System.Collections.Generic;
namespace DotaArchive {
 public sealed class CatalogDocument {
  public int SchemaVersion {get;set;}
  public string EngineSha256 {get;set;}
  public List<VersionEntry> Versions {get;set;}
  public List<PublicBuildTransition> PublicTransitions {get;set;}
 }
 public sealed class VersionEntry {
  public string Id {get;set;}
  public string Patch {get;set;}
  public string Date {get;set;}
  public string Title {get;set;}
  public string BuildId {get;set;}
  public string PublishedAtUtc {get;set;}
  public string ClientVersion {get;set;}
  public string FolderName {get;set;}
  public string Description {get;set;}
  public string SourceUrl {get;set;}
  public long TotalBytes {get;set;}
  public List<DepotEntry> Depots {get;set;}
  public List<FileEntry> Files {get;set;}
  public List<string> Directories {get;set;}
  public CompatibilitySpec Compatibility {get;set;}
  public string Executable {get;set;}
  public string LaunchArguments {get;set;}
  public override string ToString() {return Patch+"  ·  "+Date;}
 }
 public sealed class DepotEntry {
  public string DepotId {get;set;}
  public string ManifestId {get;set;}
  public string Name {get;set;}
  public long Bytes {get;set;}
 }
 public sealed class PublicBuildTransition {
  public string PreviousBuildId {get;set;}
  public string PreviousPublishedAtUtc {get;set;}
  public string BuildId {get;set;}
  public string PublishedAtUtc {get;set;}
  public string VersionId {get;set;}
  public string SourceUrl {get;set;}
 }
 public sealed class TimelineResult {
  public string Status {get;set;}
  public string Message {get;set;}
  public PublicBuildTransition Transition {get;set;}
  public VersionEntry Version {get;set;}
  public DateTimeOffset RequestedUtc {get;set;}
 }
 public sealed class FileEntry {
  public string Path {get;set;}
  public long Size {get;set;}
  public string Sha1 {get;set;}
 }
 public sealed class CompatibilitySpec {
  public string Description {get;set;}
  public List<PatchSpec> Patches {get;set;}
  public List<CopySpec> Copies {get;set;}
 }
 public sealed class PatchSpec {
  public string Path {get;set;}
  public string OriginalSha1 {get;set;}
  public string PatchedSha1 {get;set;}
  public int[] Offsets {get;set;}
  public int[] BeforeBytes {get;set;}
  public int[] AfterBytes {get;set;}
 }
 public sealed class CopySpec {
  public string Source {get;set;}
  public string Target {get;set;}
  public string Sha1 {get;set;}
 }
 public sealed class ProgressInfo {
  public string Phase {get;set;}
  public string Message {get;set;}
  public string CurrentFile {get;set;}
  public double Percent {get;set;}
  public int CompletedDepots {get;set;}
  public int TotalDepots {get;set;}
 }
 public sealed class VerificationResult {
  public bool Success {get;set;}
  public bool UsesCompatibility {get;set;}
  public int CheckedFiles {get;set;}
  public int FailedFiles {get;set;}
  public long VerifiedBytes {get;set;}
  public string Summary {get;set;}
  public List<string> Errors {get;set;}
 }
}
