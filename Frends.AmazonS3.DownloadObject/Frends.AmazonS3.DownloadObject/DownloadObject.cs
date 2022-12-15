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
using System.Reflection;
using System.Runtime.Loader;

namespace Frends.AmazonS3.DownloadObject;

/// <summary>
/// Amazon S3 Task.
/// </summary>
public class AmazonS3
{

    /// For mem cleanup.
    static AmazonS3()
    {
        var currentAssembly = Assembly.GetExecutingAssembly();
        var currentContext = AssemblyLoadContext.GetLoadContext(currentAssembly);
        if (currentContext != null)
            currentContext.Unloading += OnPluginUnloadingRequested;
    }
    
    private static readonly HttpClient Client = new();

    /// <summary>
    /// Download object(s) from AWS S3.
    /// [Documentation](https://tasks.frends.com/tasks#frends-tasks/Frends.AmazonS3.DownloadObject)
    /// </summary>
    /// <param name="input">Input parameters</param>
    /// <param name="cancellationToken">Token generated by Frends to stop this task.</param>
    /// <returns>List { string ObjectName, string FullPath, string Overwritten, bool SourceDeleted, string Info }</returns>
    public static async Task<Result> DownloadObject([PropertyTab] Input input, CancellationToken cancellationToken)
    {
        var result = new List<SingleResultObject>();

        if (string.IsNullOrWhiteSpace(input.DestinationDirectory)) throw new Exception($"Destination required.");

        if (input.AuthenticationMethod is AuthenticationMethod.AWSCredentials)
        {
            var mask = new Regex(input.SearchPattern.Replace(".", "[.]").Replace("*", ".*").Replace("?", "."));
            var targetPath = input.S3Directory + input.SearchPattern;
            using (AmazonS3Client client = new(input.AwsAccessKeyId, input.AwsSecretAccessKey, RegionSelection(input.Region)))
            {
                var allObjectsResponse = await client.ListObjectsAsync(input.BucketName, cancellationToken);
                foreach (var fileObject in allObjectsResponse.S3Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (mask.IsMatch(fileObject.Key.Split('/').Last()) && (targetPath.Split('/').Length == fileObject.Key.Split('/').Length || !input.DownloadFromCurrentDirectoryOnly) && !fileObject.Key.EndsWith("/") && fileObject.Key.StartsWith(input.S3Directory))
                    {
                        var fullPath = Path.Combine(input.DestinationDirectory, fileObject.Key.Split('/').Last());
                        result.Add(await WriteToFile(client, fileObject, input, null, fullPath, null, cancellationToken));
                    }
                }
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(input.PreSignedURL))
                throw new Exception("AWS pre-signed URL required.");
            
            var responseStream = await Client.GetStreamAsync(input.PreSignedURL, cancellationToken);
            var nameFromURI = Regex.Match(input.PreSignedURL, @"[^\/]+(?=\?)");
            var fileName = nameFromURI.Value;
            var path = Path.Combine(input.DestinationDirectory, fileName);
            result.Add(await WriteToFile(null, null, input, fileName, path, responseStream, cancellationToken));
            responseStream.Dispose();
        }

        if (result.Count == 0 && input.ThrowErrorIfNoMatch)
            throw new Exception("No matches found with search pattern");
        
        return new Result(true, result);
    }

    private static async Task<SingleResultObject> WriteToFile(AmazonS3Client amazonS3Client, S3Object fileObject, Input input, string fileName, string fullPath, Stream responseStream, CancellationToken cancellationToken)
    {
        var file = fileObject != null ? fileObject.Key.Split('/').Last() : fileName;
        var fileExists = File.Exists(fullPath);
        var sourceDeleted = false;

        try
        {
            if (fileExists && input.DestinationFileExistsAction is DestinationFileExistsAction.Error)
                throw new Exception($"File {file} already exists in {fullPath}.");

            if (fileExists && input.DestinationFileExistsAction is DestinationFileExistsAction.Info)
                return new SingleResultObject(file, fullPath, false, sourceDeleted, "Object skipped because file already exists in destination.");

            if (!fileExists || (fileExists && input.DestinationFileExistsAction is DestinationFileExistsAction.Overwrite))
            {
                string responseBody;
                Directory.CreateDirectory(input.DestinationDirectory);

                if (amazonS3Client != null)
                {
                    var request = new GetObjectRequest { BucketName = input.BucketName, Key = fileObject.Key };

                    using var response = await amazonS3Client.GetObjectAsync(request, cancellationToken);
                    using var s3responseStream = response.ResponseStream;
                    using var reader = new StreamReader(s3responseStream); 
                    responseBody = await reader.ReadToEndAsync();
                }
                else
                {
                    using var reader = new StreamReader(responseStream);
                    responseBody = await reader.ReadToEndAsync();
                }

                if (!fileExists || (fileExists && input.DestinationFileExistsAction is DestinationFileExistsAction.Overwrite && !FileLocked(input.FileLockedRetries, fullPath, cancellationToken)))
                {
                    File.WriteAllText(fullPath, responseBody);

                    if (input.DeleteSourceObject)
                        sourceDeleted = await DeleteSourceFile(amazonS3Client, input.BucketName, fileObject.Key, cancellationToken);
                }
                else
                    throw new Exception($"WriteToFile error: An unexpected error.");

                if (File.Exists(fullPath))
                    return new SingleResultObject(file, fullPath, input.DestinationFileExistsAction is DestinationFileExistsAction.Overwrite, sourceDeleted, null );
                else
                    throw new Exception("WriteToFile error: An unexpected error.");
            }
            throw new Exception("WriteToFile error: An unexpected error.");
        }
        catch (Exception ex)
        {
            throw new Exception($"WriteToFile error: {ex}");
        }
    }

    private static bool FileLocked(int fileLockedRetries, string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            for(var i = 0; i <= fileLockedRetries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream inputStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                if (inputStream.Length > 0)
                    return false;
                else
                    Thread.Sleep(1000);
            }
            
            throw new Exception($"FileLocked error: {fullPath} was locked. Max Input.FileLockedRetries = {fileLockedRetries} exceeded.");
        }
        catch (Exception ex)
        {
            throw new Exception($"FileLocked error: {ex}");
        }
    }

    private static async Task<bool> DeleteSourceFile(AmazonS3Client client, string bucketName, string key, CancellationToken cancellationToken)
    {
        try
        {
            var deleteObjectRequest = new DeleteObjectRequest
            {
                BucketName = bucketName,
                Key = key
            };

            await client.DeleteObjectAsync(deleteObjectRequest, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            throw new Exception($"DeleteSourceFile error: {ex}");
        }
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

    private static void OnPluginUnloadingRequested(AssemblyLoadContext obj)
    {
        obj.Unloading -= OnPluginUnloadingRequested;
    }
}