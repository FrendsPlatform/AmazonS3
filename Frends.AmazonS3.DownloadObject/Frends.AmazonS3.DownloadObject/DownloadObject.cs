﻿using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Linq;
using Frends.AmazonS3.DownloadObject.Definitions;

namespace Frends.AmazonS3.DownloadObject;

/// <summary>
/// Amazon S3 task.
/// </summary>
public class AmazonS3
{
    /// <summary>
    /// Download object(s) from AWS S3.
    /// [Documentation](https://tasks.frends.com/tasks#frends-tasks/Frends.AmazonS3.DownloadObject)
    /// </summary>
    /// <param name="input">Input parameters</param>
    /// <param name="cancellationToken">Token generated by Frends to stop this task.</param>
    /// <returns>List { string ObjectData }</returns>
    public static async Task<Result> DownloadObject([PropertyTab] Input input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.DestinationDirectory)) throw new Exception($"Destination required. {input.DestinationDirectory}");

        switch (input.AuthenticationMethod)
        {
            case AuthenticationMethod.AWSCredentials:
                if (string.IsNullOrWhiteSpace(input.AwsAccessKeyId) || string.IsNullOrWhiteSpace(input.AwsSecretAccessKey) || string.IsNullOrWhiteSpace(input.BucketName) || string.IsNullOrWhiteSpace(input.BucketName))
                    throw new Exception("AWS Access Key Id and Secret Access Key required.");
                return new Result { Results = await DownloadUtility(input, cancellationToken) };

            case AuthenticationMethod.PreSignedURL:
                if (string.IsNullOrWhiteSpace(input.PreSignedURL))
                    throw new Exception("AWS pre-signed URL required.");
                return new Result { Results = await DownloadUtility(input, cancellationToken) };
        }
        return null;
    }


    private static async Task<List<SingleResultObject>> DownloadUtility(Input input, CancellationToken cancellationToken)
    {
        var results = new List<SingleResultObject>();

        switch (input.AuthenticationMethod)
        {
            case AuthenticationMethod.AWSCredentials:
                var mask = new Regex(input.SearchPattern.Replace(".", "[.]").Replace("*", ".*").Replace("?", "."));
                var targetPath = input.S3Directory + input.SearchPattern;
                var client = new AmazonS3Client(input.AwsAccessKeyId, input.AwsSecretAccessKey, RegionSelection(input.Region));
                using (client)
                {
                    var allObjectsResponse = await client.ListObjectsAsync(input.BucketName, cancellationToken);
                    var allObjectsInDirectory = allObjectsResponse.S3Objects;

                    foreach (var fileObject in allObjectsInDirectory)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mask.IsMatch(fileObject.Key.Split('/').Last()) && (targetPath.Split('/').Length == fileObject.Key.Split('/').Length || !input.DownloadFromCurrentDirectoryOnly) && !fileObject.Key.EndsWith("/") && fileObject.Key.StartsWith(input.S3Directory))
                        {
                            var fullPath = Path.Combine(input.DestinationDirectory, fileObject.Key.Split('/').Last());

                            switch (input.DestinationFileExistsAction)
                            {
                                case DestinationFileExistsAction.Overwrite:
                                    results.Add(new SingleResultObject(await WriteToFile(input, fileObject, client, fullPath), fileObject.Key.Split('/').Last(), input.DestinationDirectory));
                                    break;
                                case DestinationFileExistsAction.Info:
                                    if (File.Exists(fullPath))
                                        results.Add(new SingleResultObject($"File {fileObject.Key.Split('/').Last()} was skipped because it already exists at {fullPath} and DestinationFileExistsAction = Info. Set DestinationFileExistsAction = Overwrite to overwrite the file.", fileObject.Key.Split('/').Last(), input.DestinationDirectory));
                                    else
                                        results.Add(new SingleResultObject(await WriteToFile(input, fileObject, client, fullPath), fileObject.Key.Split('/').Last(), input.DestinationDirectory));
                                    break;
                                case DestinationFileExistsAction.Error:
                                    if (File.Exists(fullPath))
                                        throw new Exception($"Error while downloading an object. File {fileObject.Key.Split('/').Last()} already exists at {fullPath} and DestinationFileExistsAction = Error. Set DestinationFileExistsAction = Overwrite to overwrite the file or Info to skip existing file.");
                                    else
                                        results.Add(new SingleResultObject(await WriteToFile(input, fileObject, client, fullPath), fileObject.Key.Split('/').Last(), input.DestinationDirectory));
                                    break;
                                default:
                                    break;
                            }

                            if (input.DeleteSourceObject) await DeleteSourceFile(client, input.BucketName, fileObject.Key, cancellationToken);
                        }
                    }
                }
                break;

            case AuthenticationMethod.PreSignedURL:

                var httpClient = new HttpClient();
                var responseStream = await httpClient.GetStreamAsync(input.PreSignedURL, cancellationToken);
                var nameFromURI = Regex.Match(input.PreSignedURL, @"[^\/]+(?=\?)");

                var filename = nameFromURI.Value;
                var path = Path.Combine(input.DestinationDirectory, filename);

                switch (input.DestinationFileExistsAction)
                {
                    case DestinationFileExistsAction.Overwrite:
                        results.Add(new SingleResultObject(await WriteToFilePreSigned(input, path, responseStream), filename, input.DestinationDirectory));
                        break;
                    case DestinationFileExistsAction.Info:
                        if (File.Exists(path))
                            results.Add(new SingleResultObject($"File {filename} was skipped because it already exists at {path} and DestinationFileExistsAction = Info. Set DestinationFileExistsAction = Overwrite to overwrite this file.", filename, input.DestinationDirectory));
                        else
                            results.Add(new SingleResultObject(await WriteToFilePreSigned(input, path, responseStream), filename, input.DestinationDirectory));
                        break;
                    case DestinationFileExistsAction.Error:
                        if (File.Exists(path))
                            throw new Exception($"Error while downloading an object {filename}. File {path} already exists and DestinationFileExistsAction = Error. Set DestinationFileExistsAction = Overwrite to overwrite this file or DestinationFileExistsAction = Info to skip this file.");
                        else
                            results.Add(new SingleResultObject(await WriteToFilePreSigned(input, path, responseStream), filename, input.DestinationDirectory));
                        break;
                    default:
                        break;
                }
                break;
        }

        if (results.Count == 0 && input.ThrowErrorIfNoMatch) throw new Exception($"No matches found with search pattern {input.SearchPattern}");
        return results;
    }

    private static async Task<string> WriteToFilePreSigned(Input input, string fullPath, Stream responseStream)
    {
        string responseBody;
        using (var reader = new StreamReader(responseStream)) responseBody = await reader.ReadToEndAsync();

        if (File.Exists(fullPath))
        {
            var file = new FileInfo(fullPath);

            while (IsFileLocked(file)) Thread.Sleep(1000);
            File.Delete(fullPath);
            File.WriteAllText(fullPath, responseBody);
            return $@"Download with overwrite complete: {fullPath}";
        }
        else
        {
            if (!Directory.Exists(input.DestinationDirectory)) Directory.CreateDirectory(input.DestinationDirectory);
            File.WriteAllText(fullPath, responseBody);
            return $@"Download complete: {fullPath}";
        }
    }

    private static async Task<string> WriteToFile(Input input, S3Object fileObject, AmazonS3Client s3Client, string fullPath)
    {
        string responseBody;

        var request = new GetObjectRequest
        {
            BucketName = input.BucketName,
            Key = fileObject.Key
        };

        using (var response = await s3Client.GetObjectAsync(request))
        using (var responseStream = response.ResponseStream)


        using (var reader = new StreamReader(responseStream)) responseBody = await reader.ReadToEndAsync();
        if (File.Exists(fullPath))
        {
            var file = new FileInfo(fullPath);

            while (IsFileLocked(file)) Thread.Sleep(1000);
            File.Delete(fullPath);
            File.WriteAllText(fullPath, responseBody);
            return $@"Download with overwrite complete: {fullPath}";
        }
        else
        {
            if (!Directory.Exists(input.DestinationDirectory)) Directory.CreateDirectory(input.DestinationDirectory);
            File.WriteAllText(fullPath, responseBody);
            return $@"Download complete: {fullPath}";
        }
    }

    private static async Task<string> DeleteSourceFile(AmazonS3Client client, string bucketName, string key, CancellationToken cancellationToken)
    {
        try
        {
            var deleteObjectRequest = new DeleteObjectRequest //delete source file from S3
            {
                BucketName = bucketName,
                Key = key
            };

            await client.DeleteObjectAsync(deleteObjectRequest, cancellationToken);
            return $@"Source file {key} deleted.";
        }
        catch (Exception ex) { throw new Exception($"Delete failed. {ex}"); }
    }

    private static bool IsFileLocked(FileInfo file)
    {
        FileStream stream = null;

        try
        {
            stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception)
        {
            // The file is unavailable because it is:
            // 1. Still being written to.
            // 2. Being processed by another thread.
            // 3. Does not exist (has already been processed).
            return true;
        }
        finally { stream?.Close(); }

        // File is not locked.
        return false;
    }

    private static RegionEndpoint RegionSelection(Region region)
    {
        return region switch
        {
            Region.AfSouth1 => RegionEndpoint.AFSouth1,
            Region.ApEast1 => RegionEndpoint.APEast1,
            Region.ApNortheast1 => RegionEndpoint.APNortheast1,
            Region.ApNortheast2 => RegionEndpoint.APNortheast2,
            Region.ApNortheast3 => RegionEndpoint.APNortheast3,
            Region.ApSouth1 => RegionEndpoint.APSouth1,
            Region.ApSoutheast1 => RegionEndpoint.APSoutheast1,
            Region.ApSoutheast2 => RegionEndpoint.APSoutheast2,
            Region.CaCentral1 => RegionEndpoint.CACentral1,
            Region.CnNorth1 => RegionEndpoint.CNNorth1,
            Region.CnNorthWest1 => RegionEndpoint.CNNorthWest1,
            Region.EuCentral1 => RegionEndpoint.EUCentral1,
            Region.EuNorth1 => RegionEndpoint.EUNorth1,
            Region.EuSouth1 => RegionEndpoint.EUSouth1,
            Region.EuWest1 => RegionEndpoint.EUWest1,
            Region.EuWest2 => RegionEndpoint.EUWest2,
            Region.EuWest3 => RegionEndpoint.EUWest3,
            Region.MeSouth1 => RegionEndpoint.MESouth1,
            Region.SaEast1 => RegionEndpoint.SAEast1,
            Region.UsEast1 => RegionEndpoint.USEast1,
            Region.UsEast2 => RegionEndpoint.USEast2,
            Region.UsWest1 => RegionEndpoint.USWest1,
            Region.UsWest2 => RegionEndpoint.USWest2,
            _ => RegionEndpoint.EUWest1,
        };
    }
}
