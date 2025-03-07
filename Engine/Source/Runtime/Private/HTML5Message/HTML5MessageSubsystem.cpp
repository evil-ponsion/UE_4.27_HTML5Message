// Fill out your copyright notice in the Description page of Project Settings.


#include "HTML5Message/HTML5MessageSubsystem.h"
#include "HTML5JavaScriptFx.h"
#include "GenericPlatform/GenericPlatformHttp.h"
#include "Misc/Base64.h"


#ifdef __EMSCRIPTEN__
 #include <emscripten/emscripten.h>
 #include <emscripten/html5.h>
#else
#define EMSCRIPTEN_KEEPALIVE
#endif




void (UHTML5MessageSubsystem::*SendUE)(const FString& data) = nullptr;

extern "C" {
#ifdef __EMSCRIPTEN__
	EMSCRIPTEN_KEEPALIVE
#endif
	void sendue(const char* indata)
	{

		FString indataString = UTF8_TO_TCHAR(indata);
		//FString::Printf(TEXT("%s"),ANSI_TO_TCHAR(indata));
		//(UHTML5MessageSubsystem::HTML5MessageSubsystemPtr->*SendUE)(indataString);
		UHTML5MessageSubsystem::HTML5MessageSubsystemPtr->OnMessageReceivedSignature(indataString);
	}
}



 UHTML5MessageSubsystem* UHTML5MessageSubsystem::HTML5MessageSubsystemPtr = nullptr;

bool UHTML5MessageSubsystem::ShouldCreateSubsystem(UObject* Outer) const
{
	return Super::ShouldCreateSubsystem(Outer);
}

void UHTML5MessageSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);
	HTML5MessageSubsystemPtr = this;
	SendUE = &UHTML5MessageSubsystem::OnMessageReceivedSignature;
}

void UHTML5MessageSubsystem::Deinitialize()
{
	Super::Deinitialize();
}

void UHTML5MessageSubsystem::OnMessageReceivedSignature(const FString& Message)
{
	//FString MessageString(Message);
	HTML5MessageSubsystemPtr->OnMessageReceivedDelegate.Broadcast(Message);
	
}

void UHTML5MessageSubsystem::SendMessageToJS(const FString& MessageType,const FString& Message)
{
	FString MessageString = FString::Printf(TEXT("{\"Command\":\"%s\",\"Message\":%s}"),*MessageType,*Message);
	TArray<uint8> BufferArray;
	FString SendBuff = MessageString;
	SendBuff = FGenericPlatformHttp::UrlEncode(SendBuff);
	SendBuff = FBase64::Encode(SendBuff);
	
	FTCHARToUTF8 Converter(*SendBuff);
	BufferArray.SetNum(Converter.Length());
	FMemory::Memcpy(BufferArray.GetData(), (uint8*)(ANSICHAR*)Converter.Get(), BufferArray.Num());

#ifdef __EMSCRIPTEN__ 
	UE_SendJS((char*)BufferArray.GetData(),BufferArray.Num());
#endif
}


