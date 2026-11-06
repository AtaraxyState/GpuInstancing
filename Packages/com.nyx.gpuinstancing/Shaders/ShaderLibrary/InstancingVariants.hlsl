#ifndef NYX_INSTANCING_VARIANTS_INCLUDED
#define NYX_INSTANCING_VARIANTS_INCLUDED

// Variantes d'un spawner, posées par VariantPlan.cs. Une instance dont la variante n'est pas dessinable (mesh ou matériau
// manquant, index hors liste) pointe vers la variante désactivée : jamais évaluée ni dessinée, les index restent stables.

#define NYX_MAX_VARIANTS 64         // = MeshSpawner.MaxVariants

uint _NyxVariantCount;              // variantes du spawner
uint _NyxValidVariantsLow;          // bits des variantes dessinables (0..31, 32..63)
uint _NyxValidVariantsHigh;
uint _NyxDeadVariant;               // variante désactivée (= nombre de variantes) : instance invalide ou détruite

bool NyxVariantValid(uint variant)
{
    uint bits = variant < 32 ? _NyxValidVariantsLow : _NyxValidVariantsHigh;
    return variant < _NyxVariantCount && ((bits >> (variant & 31)) & 1) != 0;
}

#endif // NYX_INSTANCING_VARIANTS_INCLUDED
