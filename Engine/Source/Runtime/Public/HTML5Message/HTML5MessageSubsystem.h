// Fill out your copyright notice in the Description page of Project Settings.

#pragma once

#include "CoreMinimal.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "HTML5MessageSubsystem.generated.h"

/**
 * 
 */

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FHTML5MessageDelegate, FString, Message);
UCLASS(DisplayName = "HTML5Message Subsystem")
class ENGINE_API UHTML5MessageSubsystem : public UGameInstanceSubsystem
{
	GENERATED_BODY()
public:
	virtual bool ShouldCreateSubsystem(UObject* Outer) const override;
	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;

public:

	UPROPERTY(BlueprintAssignable, Category = "HTML5Message")
	FHTML5MessageDelegate OnMessageReceivedDelegate;
	
	static UHTML5MessageSubsystem* HTML5MessageSubsystemPtr;

	void OnMessageReceivedSignature(const FString& Message);

	void SendMessageToJS(const FString& MessageType,const FString& Message);
	
};
