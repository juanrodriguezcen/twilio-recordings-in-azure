using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;

namespace TwilioRecordings.Controllers
{
    public class RecordingsController : Controller
    {
        //This action let you stream a twilio recording, no matter if its on twilio or if it was already moved to azure, its transparent for the user, you only need to know the recording SID. It supports seeking through long files.
        public ActionResult GetRecording(string sid, bool? checkedTW)
        {
            //If we already checked that the file doesnt exists in twilio, go straight to azure (this is because while the user seeks through the file, multiple requests to this action will be done, so we dont check twilio each time)
            if (!checkedTW.HasValue)
            {
                try
                {
                    //first check if the file exists in twilio. By doing a HEAD request we dont download the whole file, we just check if it exists, which is much faster
                    string recTwilioUrl = "http://api.twilio.com/2010-04-01/Accounts/" + ConfigurationManager.AppSettings["Twilio.AccountSID"] + "/Recordings/" + sid;
                    WebRequest request = WebRequest.Create(new Uri(recTwilioUrl));
                    request.Method = "HEAD";

                    using (WebResponse response = request.GetResponse())
                    {
                        if (response.ContentLength > 0)
                        {
                            return Redirect(recTwilioUrl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    //The file doesnt exist in twilio, try with azure
                    return RedirectToRoute("GetRecording", new { sid, checkedTW = true });
                }
            }


            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["Azure.StorageConnectionString"]);
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

            CloudBlobContainer container = blobClient.GetContainerReference(ConfigurationManager.AppSettings["Azure.ContainerName"]);
            Response.AddHeader("Content-Disposition", "filename=" + sid + ".wav"); // force download
            Response.AddHeader("Content-Type", "audio/x-wav");

            var blobReference = container.GetBlockBlobReference(sid + ".wav");
            blobReference.FetchAttributes();

            
            //DISCLAIMER: The rest of the code of this action was not written by me. This code handles the http requests for file seeking. I only make tiny updates so it uses the stream from an Azure blob instead of a local file
            //I grabbed it from: http://blogs.visigo.com/chriscoulson/easy-handling-of-http-range-requests-in-asp-net/

            long size, start, end, length, fp = 0;
            size = blobReference.Properties.Length;
            start = 0;
            end = size - 1;
            length = size;
            // Now that we've gotten so far without errors we send the accept range header
            /* At the moment we only support single ranges.
             * Multiple ranges requires some more work to ensure it works correctly
             * and comply with the spesifications: http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html#sec19.2
             *
             * Multirange support annouces itself with:
             * header('Accept-Ranges: bytes');
             *
             * Multirange content must be sent with multipart/byteranges mediatype,
             * (mediatype = mimetype)
             * as well as a boundry header to indicate the various chunks of data.
             */
            Response.AddHeader("Accept-Ranges", "0-" + size);
            // header('Accept-Ranges: bytes');
            // multipart/byteranges
            // http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html#sec19.2

            if (!String.IsNullOrEmpty(Request.ServerVariables["HTTP_RANGE"]))
            {
                long anotherStart = start;
                long anotherEnd = end;
                string[] arr_split = Request.ServerVariables["HTTP_RANGE"].Split(new char[] { Convert.ToChar("=") });
                string range = arr_split[1];

                // Make sure the client hasn't sent us a multibyte range
                if (range.IndexOf(",") > -1)
                {
                    // (?) Shoud this be issued here, or should the first
                    // range be used? Or should the header be ignored and
                    // we output the whole content?
                    Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + size);
                    throw new HttpException(416, "Requested Range Not Satisfiable");

                }

                // If the range starts with an '-' we start from the beginning
                // If not, we forward the file pointer
                // And make sure to get the end byte if spesified
                if (range.StartsWith("-"))
                {
                    // The n-number of the last bytes is requested
                    anotherStart = size - Convert.ToInt64(range.Substring(1));
                }
                else
                {
                    arr_split = range.Split(new char[] { Convert.ToChar("-") });
                    anotherStart = Convert.ToInt64(arr_split[0]);
                    long temp = 0;
                    anotherEnd = (arr_split.Length > 1 && Int64.TryParse(arr_split[1].ToString(), out temp)) ? Convert.ToInt64(arr_split[1]) : size;
                }
                /* Check the range and make sure it's treated according to the specs.
                 * http://www.w3.org/Protocols/rfc2616/rfc2616-sec14.html
                 */
                // End bytes can not be larger than $end.
                anotherEnd = (anotherEnd > end) ? end : anotherEnd;
                // Validate the requested range and return an error if it's not correct.
                if (anotherStart > anotherEnd || anotherStart > size - 1 || anotherEnd >= size)
                {

                    Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + size);
                    throw new HttpException(416, "Requested Range Not Satisfiable");
                }
                start = anotherStart;
                end = anotherEnd;

                length = end - start + 1; // Calculate new content length
                Response.StatusCode = 206;
            }

            // Notify the client the byte range we'll be outputting
            Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + size);
            Response.AddHeader("Content-Length", length.ToString());
            // Start buffered download
            blobReference.DownloadRangeToStream(Response.OutputStream, start, length);


            return new EmptyResult();
        }

    }
}
