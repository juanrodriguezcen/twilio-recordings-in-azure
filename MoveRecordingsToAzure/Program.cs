using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Twilio;

namespace MoveRecordingsToAzure
{
    class Program
    {
        /// <summary>
        /// Calculates the MD5 checksum of a file
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        private static string GetFileMDS(string filePath)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    return BitConverter.ToString(md5.ComputeHash(stream));
                }
            }
        }

        /// <summary>
        /// Get a page of recordings to move to azure (we do this in a paginated way)
        /// </summary>
        /// <returns>A list of recordings, which includes the most relevant information of the recording</returns>
        private static Tuple<List<TwilioRecordingInfo>, int> GetRecordingsToMove()
        {
            //I've seen that sometimes twilio fails to response to the  "get recordings" api call, it times out. However if I try a couple of times, it works, so we do up to 10 retries
            int retries = 10;
            while (retries > 0)
            {
                try
                {
                    using (WebClient client = new WebClient())
                    {
                        //We only move recordings that are older than XX days. You can set it to 0 in the web.config if you want to move all the existing recordings to azure.
                        int daysOldRequired = int.Parse(ConfigurationManager.AppSettings["GetRecordings.DaysOldRequired"]);
                        int pageSize = int.Parse(ConfigurationManager.AppSettings["GetRecordings.PageSize"]);
                        DateTime moveRecordingsOlderThan = DateTime.Today.AddDays(-daysOldRequired);

                        string getRecordingsUrl = "https://api.twilio.com/2010-04-01/Accounts/" + ConfigurationManager.AppSettings["Twilio.AccountSID"] + "/Recordings.json?PageSize=" + pageSize + "&DateCreated<=" + moveRecordingsOlderThan.ToString("yyyy-MM-dd");

                        NetworkCredential credentials = new NetworkCredential();
                        credentials.UserName = ConfigurationManager.AppSettings["Twilio.AccountSID"];
                        credentials.Password = ConfigurationManager.AppSettings["Twilio.AuthToken"];
                        client.Credentials = credentials;

                        string response = client.DownloadString(getRecordingsUrl);
                        dynamic responseJson = JObject.Parse(response);

                        if (((int)responseJson.total) == 0)
                        {
                            return new Tuple<List<TwilioRecordingInfo>, int>(new List<TwilioRecordingInfo>(), 0); //There is nothing to process
                        }
                        else
                        {
                            string lastPageUri = (string)responseJson.last_page_uri;

                            string lastPageResponse = client.DownloadString("https://api.twilio.com" + lastPageUri);
                            dynamic lastPageResponseJson = JObject.Parse(response);

                            List<TwilioRecordingInfo> recordingSids = new List<TwilioRecordingInfo>();
                            foreach (var recording in lastPageResponseJson.recordings)
                            {
                                TwilioRecordingInfo recInfo = new TwilioRecordingInfo();
                                recInfo.SID = (string)recording.sid;
                                recInfo.CallSID = (string)recording.call_sid;
                                recInfo.DateCreated = (string)recording.date_created;
                                recInfo.Duration = (int)recording.duration;

                                recordingSids.Add(recInfo);
                            }

                            return new Tuple<List<TwilioRecordingInfo>, int>(recordingSids, (int)lastPageResponseJson.total);
                        }
                    }
                }
                catch (Exception)
                {
                    retries--;
                    System.Threading.Thread.Sleep(2000); //Wait 2 seconds before retrying
                }
            }

            throw new Exception("Too many retries"); //Stop the execution, too many retries... twilio might not be responding.
        }

        /// <summary>
        /// Uploads the recording from twilio to azure and after that it checks that the content saved in azure matches the content in twilio
        /// </summary>
        /// <param name="recording"></param>
        /// <returns></returns>
        private static bool UploadAndCheckRecording(TwilioRecordingInfo recording)
        {
            string fileName = recording.SID + ".wav";
            string filePath = Path.Combine("TempRecordings", fileName);
            string contentType = null; //We are going to try to use the content type that twilio send us

            //Download the recording from twilio
            using (WebClient client = new WebClient())
            {
                string recordingUrl = "https://api.twilio.com/2010-04-01/Accounts/" + ConfigurationManager.AppSettings["Twilio.AccountSID"] + "/Recordings/" + recording.SID;
                client.DownloadFile(recordingUrl, filePath);

                if (client.ResponseHeaders.AllKeys.Contains("Content-Type"))
                {
                    contentType = client.ResponseHeaders["Content-Type"];
                }
            }


            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["Azure.StorageConnectionString"]);
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            CloudBlobContainer container = blobClient.GetContainerReference(ConfigurationManager.AppSettings["Azure.ContainerName"]);

            BlobContainerPermissions permissions = new BlobContainerPermissions();
            permissions.PublicAccess = BlobContainerPublicAccessType.Off;

            // Create the container if it doesn't already exist.
            bool wasCreated = container.CreateIfNotExists();
            if (wasCreated)
            {
                container.SetPermissions(permissions);
            }


            //Check if this file is already uploaded
            try
            {
                CloudBlockBlob blockBlob = container.GetBlockBlobReference(fileName);
                string azureCheckFilePath = Path.Combine("TempRecordings", recording.SID + ".azure.wav");

                // Save blob contents to a file.
                using (var fileStream = System.IO.File.OpenWrite(azureCheckFilePath))
                {
                    blockBlob.DownloadToStream(fileStream);
                }

                //Check if both files are equal
                if (GetFileMDS(filePath) == GetFileMDS(azureCheckFilePath))
                {
                    return true; //The file is already uploaded and checked, so there is nothing to do, we just need to delete the recording from twilio
                }
            }
            catch (Exception)
            {
                //The blob was not found
            }

            //Upload the file to azure
            CloudBlockBlob recordingBlob = container.GetBlockBlobReference(fileName);
            using (var fileStream = System.IO.File.OpenRead(filePath))
            {
                recordingBlob.UploadFromStream(fileStream);

                if (string.IsNullOrEmpty(contentType))
                {
                    recordingBlob.Properties.ContentType = "audio/x-wav";
                }
                else
                {
                    recordingBlob.Properties.ContentType = contentType;
                }

                recordingBlob.SetProperties();

                //We set relevant information about this recording in the metadata, so we can grab it afterwards from the blob.
                recordingBlob.Metadata["CallSID"] = recording.CallSID;
                recordingBlob.Metadata["Duration"] = recording.Duration.ToString();
                recordingBlob.Metadata["DateCreatedTwilio"] = recording.DateCreated;

                recordingBlob.SetMetadata();
            }

            string azureCheckFilePath2 = Path.Combine("TempRecordings", recording.SID + ".azure2.wav");

            //Now we download what we have just uploaded, and we do an MD5 checksum comparison. We are triple checking that the same file that is on twilio is on azure, because we are going to delete it from twilio.
            using (var fileStream = System.IO.File.OpenWrite(azureCheckFilePath2))
            {
                recordingBlob.DownloadToStream(fileStream);
            }

            return GetFileMDS(filePath) == GetFileMDS(azureCheckFilePath2);
        }

        static void Main(string[] args)
        {
            List<string> sidsWithErrorsToSkip = new List<string>();
            Mutex sidsToSkipMutex = new Mutex(); 

            var toMoveInfo = GetRecordingsToMove();
            List<TwilioRecordingInfo> previousRecordings = new List<TwilioRecordingInfo>();
            while (toMoveInfo.Item2 > 0)
            {
                if (!Directory.Exists("TempRecordings"))
                {
                    Directory.CreateDirectory("TempRecordings");
                }

                //Set the max threads that you would like to use for downloading the recordings from twilio and uploading it to azure
                ParallelOptions options = new ParallelOptions();
                options.MaxDegreeOfParallelism = int.Parse(ConfigurationManager.AppSettings["MaxThreads"]); 

                Parallel.ForEach(toMoveInfo.Item1, options, recording =>
                {
                    //We found that some recordings are returned by twilio's api but they dont really have the recording... so after we found one, we add it to this list so we dont try to download it again on next iteration
                    bool skip = false;
                    sidsToSkipMutex.WaitOne();
                    if (sidsWithErrorsToSkip.Contains(recording.SID))
                    {
                        skip = true;
                    }
                    sidsToSkipMutex.ReleaseMutex();

                    if (!skip)
                    {
                        try
                        {
                            var uploaded = UploadAndCheckRecording(recording);
                            if (uploaded)
                            {
                                //The call is only deleted if its uploaded (double checked with a download and md5 comparison)
                                var twilioClient = new TwilioRestClient(ConfigurationManager.AppSettings["Twilio.AccountSID"], ConfigurationManager.AppSettings["Twilio.AuthToken"]);
                                var deleteStatus = twilioClient.DeleteRecording(recording.SID);
                            }
                        }
                        catch (WebException wex)
                        {
                            if (wex.Response != null && wex.Response is HttpWebResponse && ((HttpWebResponse)wex.Response).StatusCode == HttpStatusCode.NotFound)
                            {
                                //We add this recording sid to the list of sids to skip
                                sidsToSkipMutex.WaitOne();
                                sidsWithErrorsToSkip.Add(recording.SID);
                                sidsToSkipMutex.ReleaseMutex();
                            }
                        }
                        catch (Exception ex){ } //Something happened while processing this call, we just skip it and in the next page of recordings, it will be processed again
                    }
                });

                //After each iteration we delete the temp recordings folder to free hdd space
                if (Directory.Exists("TempRecordings"))
                {
                    Directory.Delete("TempRecordings", true);
                }

                previousRecordings = toMoveInfo.Item1;

                //Get the next page of recordings to process
                toMoveInfo = GetRecordingsToMove();

                //Check if in the new "page" the recordings are the same as in the previous one, if this is the case, we need to stop or we would enter in an infinit loop
                if (toMoveInfo.Item2 > 0 && !toMoveInfo.Item1.Any(a => !previousRecordings.Any(b => b.SID == a.SID)))
                {
                    //There only recordings left are the ones in the sidsToSkipMutex, so we stop
                    break;
                }
            }


        }
    }
}
