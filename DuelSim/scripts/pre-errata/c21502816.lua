--ライトロード・ハンター ライコウ (Pre-Errata)
--Ryko, Lightsworn Hunter (Pre-Errata) - Omega id 21502816, plays as 21502796
--"FLIP: You can destroy 1 card on the field. Send the top 3 cards of your Deck to the Graveyard."
--Pre-errata ruling (Edison format): the card to destroy is TARGETED when the effect activates (optional); on
--resolution, mill 3, then destroy the target if it's still there.
--The current card (c21502796) doesn't target: it picks the card during resolution.
local s,id=GetID()
function s.initial_effect(c)
	--flip
	local e1=Effect.CreateEffect(c)
	e1:SetDescription(aux.Stringid(21502796,0))
	e1:SetCategory(CATEGORY_DECKDES)
	e1:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_FLIP)
	e1:SetProperty(EFFECT_FLAG_CARD_TARGET)
	e1:SetTarget(s.target)
	e1:SetOperation(s.operation)
	c:RegisterEffect(e1)
end
function s.target(e,tp,eg,ep,ev,re,r,rp,chk,chkc)
	if chkc then return chkc:IsOnField() end
	if chk==0 then return true end
	e:SetCategory(CATEGORY_DECKDES)
	--As Omega asks it: no yes/no, just a forced "select 1-1 for DESTROY" when there is any card on the field
	--(seen in a real Omega log, 2026-11-06_180537_Vayu.log #067).
	if Duel.IsExistingTarget(nil,tp,LOCATION_ONFIELD,LOCATION_ONFIELD,1,nil) then
		Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_DESTROY)
		local g=Duel.SelectTarget(tp,nil,tp,LOCATION_ONFIELD,LOCATION_ONFIELD,1,1,nil)
		--SelectTarget returns nil when nothing is chosen
		if g and #g>0 then
			e:SetCategory(CATEGORY_DECKDES+CATEGORY_DESTROY)
			Duel.SetOperationInfo(0,CATEGORY_DESTROY,g,1,0,0)
		end
	end
	Duel.SetOperationInfo(0,CATEGORY_DECKDES,nil,0,tp,3)
end
function s.operation(e,tp,eg,ep,ev,re,r,rp)
	Duel.DiscardDeck(tp,3,REASON_EFFECT)
	local tc=Duel.GetFirstTarget()
	if tc and tc:IsRelateToEffect(e) then
		Duel.Destroy(tc,REASON_EFFECT)
	end
end
