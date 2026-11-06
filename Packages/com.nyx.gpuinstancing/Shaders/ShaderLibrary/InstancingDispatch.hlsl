#ifndef NYX_INSTANCING_DISPATCH_INCLUDED
#define NYX_INSTANCING_DISPATCH_INCLUDED

// Noyaux « une instance par thread », lancés par SpawnerGpu.DispatchInstances (MeshSpawner.DispatchInstances) : groupes de
// NYX_THREADS threads, répartis en 2D au-delà de 65 535 groupes. _NyxGroupsX permet de retrouver l'index de l'instance.

#define NYX_THREADS 64              // = SpawnerGpu.ThreadGroupSize

uint _NyxGroupsX;                   // groupes en X (dispatch 2D au-delà de 65 535 groupes)

// Index du groupe dans le dispatch 2D.
uint NyxGroupIndex(uint3 groupId)
{
    return groupId.y * _NyxGroupsX + groupId.x;
}

// Index de l'instance du thread : (groupe.y × _NyxGroupsX + groupe.x) × NYX_THREADS + thread.
uint NyxInstanceIndex(uint3 groupId, uint3 groupThreadId)
{
    return NyxGroupIndex(groupId) * NYX_THREADS + groupThreadId.x;
}

#endif // NYX_INSTANCING_DISPATCH_INCLUDED
