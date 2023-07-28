﻿using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Frends.AmazonS3.CreateBucket.Definitions;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Frends.AmazonS3.CreateBucket;

/// <summary>
/// Amazon S3 Task.
/// </summary>
public class AmazonS3
{
    /// <summary>
    /// Create AWS S3 Bucket.
    /// [Documentation](https://tasks.frends.com/tasks/frends-tasks/Frends.AmazonS3.CreateBucket)
    /// </summary>
    /// <param name="connection">Connection parameters</param>
    /// <param name="cancellationToken">Token generated by Frends to stop this Task.</param>
    /// <returns>Object { bool success, string BucketLocation } </returns>
    public static async Task<Result> CreateBucket([PropertyTab] Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            using IAmazonS3 s3Client = new AmazonS3Client(connection.AwsAccessKeyId, connection.AwsSecretAccessKey, RegionSelection(connection.Region));
            var bucketName = connection.BucketName;
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3Client, bucketName))
            {
                var putBucketRequest = new PutBucketRequest
                {
                    BucketName = bucketName,
                    UseClientRegion = true,
                    CannedACL = GetS3CannedACL(connection.ACL),
                    ObjectLockEnabledForBucket = connection.ObjectLockEnabledForBucket,
                };

                PutBucketResponse putBucketResponse = await s3Client.PutBucketAsync(putBucketRequest, cancellationToken);

                var getBucketLocationRequest = new GetBucketLocationRequest()
                {
                    BucketName = bucketName
                };
                var response = await s3Client.GetBucketLocationAsync(getBucketLocationRequest, cancellationToken);
                return new Result(true, response.Location.ToString());
            }
            else
            {
                return new Result(true, $"Bucket already exists.");
            }
        }
        catch (AmazonS3Exception e)
        {
            throw new AmazonS3Exception("Failed to create the bucket.", e);
        }
        catch (Exception e)
        {
            throw new Exception("Unexpected error occurred while creating the bucket.", e);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "can only test eu-central-1")]
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

    [ExcludeFromCodeCoverage(Justification = "can only test S3CannedACL.Private")]
    private static S3CannedACL GetS3CannedACL(ACLs acl)
    {
        return acl switch
        {
            ACLs.Private => S3CannedACL.Private,
            ACLs.PublicRead => S3CannedACL.PublicRead,
            ACLs.PublicReadWrite => S3CannedACL.PublicReadWrite,
            ACLs.AuthenticatedRead => S3CannedACL.AuthenticatedRead,
            ACLs.BucketOwnerRead => S3CannedACL.BucketOwnerRead,
            ACLs.BucketOwnerFullControl => S3CannedACL.BucketOwnerFullControl,
            ACLs.LogDeliveryWrite => S3CannedACL.LogDeliveryWrite,
            _ => S3CannedACL.NoACL,
        };
    }
}