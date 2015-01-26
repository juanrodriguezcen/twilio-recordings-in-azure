Twilio Recording in Azure
===============

Twilio is an amazing cloud communication service which excels in a lot of things, however their recording storage is quite expensive. They charge you by minute stored per month, which means that if you have a recording of 10 mins, you will be charged for those 10 mins each month as long as you keep the recording on twilio's servers. Each account gets 10,000 free minutes per month and after that they charge you $0.0005 per minute per month (details can be seen in https://www.twilio.com/help/faq/voice/how-much-does-it-cost-to-record-a-call). Although this seems pretty low, the minutes start accumulating on your account, because after 3 months, you will be paying, for all the minutes recorded during the first month, plus the ones from the second and the third months too. If you have a high traffic callcenter, you can easily reach millions on minutes in a few months.

I checked multiple recordings, and in average, 1 minute equals 1MB in the recording file. And the minute is their unit, so no matter if your recording is 90 seconds long they charge you 2 minutes for that recording, although they only have to save 1.5MB in their datacenters.

Azure pricing is per GB, and at the time of this writing, its $0.048 per GB using geographically redundancy (6 copies of each file are stored). This means that in order to store 1024000 minutes in twilio (1000GB), it costs 1024000 * $0.0005 = $512, while in azure it costs just 1000 * $0.048 = $48. In this case, you would be saving $468 dollars per month, just by moving the files to another datacenter. 

This repository contains 2 projects:

A) MoveRecordingsToAzure: This is a console program that moves recordings from your twilio account to your azure account, including metadata of the recording like the duration and when it was created.

B) TwilioRecordings: Is an Asp.net MVC website with just 1 available url (/recordings/{sid}.wav). This url allows you to stream files from twilio and azure transparently. If you have the recording's SID in your db, you can use this url and you dont have to worry if your recording was already moved to azure or if its still in twilio. This website checks if the audio is still in twilio and lets you stream from there (delegating the streaming details to twilio), and if it is not it grabs it from the corresponding azure blob and handles the streaming details by its own (allowing file seeking).

